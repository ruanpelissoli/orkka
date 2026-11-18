namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner.Cli;

/// <summary>
/// The ~/.orkka/.env → runner.env rename is a security migration: the old name sat in
/// the gemini CLI's upward dotenv walk from repo-cache worktrees, feeding the runner
/// token and Postgres password into agent processes. The migration must move the file
/// out of the walk path without ever losing a user-added key (image pins, PG port,
/// ORKKA_GEMINI_API_KEY) — and must never leave a legacy .env behind.
/// </summary>
public sealed class RunnerEnvMigrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "orkka-envmig-test-" + Ulid.NewUlid());
    private string Legacy => Path.Combine(_dir, ".env");
    private string Current => Path.Combine(_dir, "runner.env");

    public RunnerEnvMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void NoLegacyFile_IsANoOp()
    {
        RunnerEnvFile.MigrateLegacyEnvFile(Legacy, Current);

        File.Exists(Current).Should().BeFalse();
    }

    [Fact]
    public void LegacyOnly_IsMovedWholesale()
    {
        File.WriteAllText(Legacy, "ORKKA_RUNNER_TOKEN=t1\nORKKA_PG_PASSWORD=p1\nORKKA_PG_PORT=5434\n");

        RunnerEnvFile.MigrateLegacyEnvFile(Legacy, Current);

        File.Exists(Legacy).Should().BeFalse();
        var content = File.ReadAllText(Current);
        content.Should().Contain("ORKKA_RUNNER_TOKEN=t1");
        content.Should().Contain("ORKKA_PG_PORT=5434");
    }

    [Fact]
    public void BothFiles_MergesMissingKeysAndDeletesLegacy()
    {
        // An old binary re-provisioned .env after a new one had already migrated:
        // runner.env wins on conflicts, user additions in the legacy file survive.
        File.WriteAllText(Current, "ORKKA_RUNNER_TOKEN=current\n");
        File.WriteAllText(Legacy, "ORKKA_RUNNER_TOKEN=stale\nORKKA_GEMINI_API_KEY=user-added\n");

        RunnerEnvFile.MigrateLegacyEnvFile(Legacy, Current);

        File.Exists(Legacy).Should().BeFalse();
        var content = File.ReadAllText(Current);
        content.Should().Contain("ORKKA_RUNNER_TOKEN=current");
        content.Should().NotContain("stale");
        content.Should().Contain("ORKKA_GEMINI_API_KEY=user-added");
    }

    [Fact]
    public void Migration_IsIdempotent()
    {
        File.WriteAllText(Legacy, "ORKKA_RUNNER_TOKEN=t1\n");

        RunnerEnvFile.MigrateLegacyEnvFile(Legacy, Current);
        var afterFirst = File.ReadAllText(Current);
        RunnerEnvFile.MigrateLegacyEnvFile(Legacy, Current);

        File.ReadAllText(Current).Should().Be(afterFirst);
    }
}
