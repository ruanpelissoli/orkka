namespace Orkka.Tests.Git;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Git;
using Orkka.Modules.SourceControl.Domain;
using Orkka.Modules.SourceControl.Infrastructure;
using Orkka.Modules.SourceControl.Infrastructure.Services;

/// <summary>
/// The ledger row and the writer behind it: what a record becomes in the table, which records
/// are kept, and that the background writer actually lands them in batches.
/// </summary>
public sealed class GitLedgerPersistenceTests
{
    [Fact]
    public void ARecord_BecomesARow_WithIdsParsed_AndLongTextTruncated()
    {
        var org = Ulid.NewUlid();
        var record = new GitOperationRecord(
            GitOperationKind.Publish, GitTopologies.CloudWorker, org.ToString(), "not-a-ulid", null,
            DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1234.6), false, GitErrorCode.NonFastForward,
            new string('e', 5000), new string('c', 3000), 4);

        var row = GitOperation.From(record);

        row.OrgId.Should().Be(org);
        row.RepoId.Should().BeNull("an unparsable id is dropped, never thrown on");
        row.TaskId.Should().BeNull();
        row.DurationMs.Should().Be(1234);
        row.Error!.Length.Should().Be(2000);
        row.LastCommand!.Length.Should().Be(1000);
        row.Code.Should().Be(GitErrorCode.NonFastForward);
        row.CommandCount.Should().Be(4);
    }

    [Fact]
    public void TheDbLedger_KeepsFailures_AndEveryNonReadSuccess_ButDropsSuccessfulReads()
    {
        var ledger = new DbGitOperationLedger(new GitTopology(GitTopologies.ApiHost), NullLogger<DbGitOperationLedger>.Instance);

        ledger.Record(Record(GitOperationKind.Read, succeeded: true));
        ledger.Record(Record(GitOperationKind.Read, succeeded: false));
        ledger.Record(Record(GitOperationKind.Publish, succeeded: true));

        var queued = new List<GitOperationRecord>();
        while (ledger.Reader.TryRead(out var r)) queued.Add(r);
        queued.Select(r => (r.Kind, r.Succeeded)).Should().Equal(
            (GitOperationKind.Read, false), (GitOperationKind.Publish, true));
        ledger.Topology.Should().Be(GitTopologies.ApiHost);
    }

    [Fact]
    public async Task TheWriter_LandsQueuedRecords_InTheTable()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<SourceControlDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();

        var ledger = new DbGitOperationLedger(new GitTopology(GitTopologies.CloudWorker), NullLogger<DbGitOperationLedger>.Instance);
        var writer = new GitOperationLedgerWriter(ledger, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GitOperationLedgerWriter>.Instance);

        await writer.StartAsync(CancellationToken.None);
        try
        {
            ledger.Record(Record(GitOperationKind.SyncBase, succeeded: false));
            ledger.Record(Record(GitOperationKind.CreateWorkspace, succeeded: true));

            var deadline = DateTime.UtcNow.AddSeconds(15);
            int count;
            do
            {
                await Task.Delay(250);
                using var scope = provider.CreateScope();
                count = await scope.ServiceProvider.GetRequiredService<SourceControlDbContext>().GitOperations.CountAsync();
            } while (count < 2 && DateTime.UtcNow < deadline);

            count.Should().Be(2);
            using var check = provider.CreateScope();
            var rows = await check.ServiceProvider.GetRequiredService<SourceControlDbContext>().GitOperations.ToListAsync();
            rows.Should().Contain(r => r.Kind == GitOperationKind.SyncBase && !r.Succeeded && r.Topology == GitTopologies.CloudWorker);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    private static GitOperationRecord Record(GitOperationKind kind, bool succeeded) => new(
        kind, GitTopologies.CloudWorker, null, null, null, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10),
        succeeded, succeeded ? GitErrorCode.None : GitErrorCode.Unknown, succeeded ? null : "boom", "status", 1);
}
