namespace Orkka.Tests.Features.Runners;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Runners.Domain;
using Orkka.Modules.Runners.Infrastructure;
using Orkka.Modules.Runners.Infrastructure.Services;

/// <summary>
/// A runner token is a bearer credential for the developer's machine. It expires; a runner in
/// use renews its own before then; a renewed-out token only works for a short grace period;
/// and a token copied off a machine or left on an unused one lapses by itself.
/// </summary>
public sealed class RunnerTokenLifetimeTests : IDisposable
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly Ulid OrgId = Ulid.NewUlid();
    private readonly RunnersDbContext _db;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly RunnerHubAuth _auth;

    public RunnerTokenLifetimeTests()
    {
        _db = new RunnersDbContext(
            new DbContextOptionsBuilder<RunnersDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            currentUser: null);
        _auth = new RunnerHubAuth(_db, _clock);
    }

    public void Dispose() => _db.Dispose();

    private async Task<(RunnerToken Row, string Raw)> IssueAsync(TimeSpan expiresIn)
    {
        var raw = RunnerTokenValues.Generate();
        var row = new RunnerToken
        {
            OrgId = OrgId, UserId = "user_dev", Name = "Work laptop",
            TokenHash = RunnerTokenValues.Hash(raw), ExpiresAt = _clock.Now + expiresIn,
        };
        _db.RunnerTokens.Add(row);
        await _db.SaveChangesAsync();
        return (row, raw);
    }

    [Fact]
    public async Task AnExpiredToken_DoesNotConnect_AndALiveConnectionDropsAtTheNextHello()
    {
        var (row, raw) = await IssueAsync(TimeSpan.FromDays(1));
        (await _auth.ValidateBearerAsync($"Bearer {raw}")).Should().NotBeNull();

        _clock.Now += TimeSpan.FromDays(2);

        (await _auth.ValidateBearerAsync($"Bearer {raw}")).Should().BeNull();
        (await _auth.IsUnusableAsync(row.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task ATokenFarFromExpiry_IsNotRotated()
    {
        var (row, _) = await IssueAsync(RunnerTokenValues.Lifetime);

        var result = await _auth.RotateIfDueAsync(row.Id);

        result!.NewToken.Should().BeNull();
        (await _db.RunnerTokens.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ATokenNearExpiry_IsRenewed_AndTheOldOneOnlyHasTheGraceLeft()
    {
        var (row, oldRaw) = await IssueAsync(TimeSpan.FromDays(10));

        var result = await _auth.RotateIfDueAsync(row.Id);

        result!.NewToken.Should().StartWith(RunnerTokenValues.Prefix).And.NotBe(oldRaw);
        result.ExpiresAt.Should().Be(_clock.Now + RunnerTokenValues.Lifetime);
        (await _auth.ValidateBearerAsync($"Bearer {result.NewToken}")).Should().NotBeNull();

        // The runner still holds the old one until it redials: it keeps working briefly…
        (await _auth.ValidateBearerAsync($"Bearer {oldRaw}")).Should().NotBeNull();
        // …and is worthless once the grace is over.
        _clock.Now += RunnerTokenValues.RotationGrace + TimeSpan.FromMinutes(1);
        (await _auth.ValidateBearerAsync($"Bearer {oldRaw}")).Should().BeNull();
        (await _auth.ValidateBearerAsync($"Bearer {result.NewToken}")).Should().NotBeNull();
    }

    [Fact]
    public async Task ARevokedToken_CannotBeRotatedIntoANewOne()
    {
        var (row, _) = await IssueAsync(TimeSpan.FromDays(5));
        row.RevokedAt = _clock.Now;
        await _db.SaveChangesAsync();

        (await _auth.RotateIfDueAsync(row.Id)).Should().BeNull();
        (await _db.RunnerTokens.CountAsync()).Should().Be(1);
    }
}
