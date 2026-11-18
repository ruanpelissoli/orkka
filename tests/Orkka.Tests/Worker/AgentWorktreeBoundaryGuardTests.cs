namespace Orkka.Tests.Worker;

using System.Text.Json;
using Orkka.Modules.Execution.Agents;
using FluentAssertions;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The file tools may only write under the task's worktree. The incident that motivated this:
/// an agent took `/home/user/api` from the repo's Dockerfile as the repo root, Write landed at
/// `C:\home\user\api\…` on a Windows runner, the live diff (correctly) showed nothing and the
/// agent reported done.
/// </summary>
public sealed class AgentWorktreeBoundaryGuardTests
{
    private static PreToolUseHookInput Input(string tool, string cwd, object toolInput) => new()
    {
        SessionId = "test-session",
        TranscriptPath = "/tmp/transcript",
        Cwd = cwd,
        ToolName = tool,
        ToolInput = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(toolInput)),
    };

    private static async Task<(bool Allowed, string Reason, List<(string Tool, string Path)> Blocked)> Run(
        string worktree, string cwd, string tool, object toolInput, bool ignoreCase = false)
    {
        var blocked = new List<(string, string)>();
        var guard = new AgentWorktreeBoundaryGuard(worktree, NullLogger.Instance,
            onBlocked: (t, p) => { blocked.Add((t, p)); return Task.CompletedTask; },
            ignoreCase: ignoreCase);
        var output = await guard.Evaluate(Input(tool, cwd, toolInput), toolUseId: null, context: null);
        var allowed = output.Decision == "approve";
        return (allowed, output.Reason ?? "", blocked);
    }

    [Fact]
    public async Task TheIncident_WindowsRunner_DockerfileWorkdirPath_IsDenied()
    {
        const string worktree = @"C:\Projects\waveborne\api-worktrees\WAV-28";
        var (allowed, reason, blocked) = await Run(worktree, worktree, "Write",
            new { file_path = "/home/user/api/docs/LIVE_DIFF_SMOKE.md", content = "x" }, ignoreCase: true);

        allowed.Should().BeFalse();
        reason.Should().Contain("outside the task's worktree").And.Contain("Dockerfiles");
        blocked.Should().ContainSingle().Which.Should().Be(("Write", "/home/user/api/docs/LIVE_DIFF_SMOKE.md"));
    }

    [Theory]
    [InlineData("docs/NOTES.md")]
    [InlineData("./src/Program.cs")]
    [InlineData("src/../README.md")]
    [InlineData("/repos/org/repo/worktrees/T-1/src/a.cs")]
    [InlineData("/repos/org/repo/worktrees/T-1")]
    public async Task Posix_InsideTheWorktree_IsAllowed(string path)
    {
        const string worktree = "/repos/org/repo/worktrees/T-1";
        var (allowed, _, blocked) = await Run(worktree, worktree, "Edit",
            new { file_path = path, old_string = "a", new_string = "b" });

        allowed.Should().BeTrue(path);
        blocked.Should().BeEmpty();
    }

    [Theory]
    [InlineData("../other-task/a.cs")]
    [InlineData("../../secrets.env")]
    [InlineData("/repos/org/repo/worktrees/T-2/a.cs")]
    [InlineData("/repos/org/repo/worktrees/T-10/a.cs")]   // prefix of the name, not the dir
    [InlineData("/etc/passwd")]
    [InlineData("~/.ssh/config")]
    public async Task Posix_OutsideTheWorktree_IsDenied(string path)
    {
        const string worktree = "/repos/org/repo/worktrees/T-1";
        var (allowed, _, blocked) = await Run(worktree, worktree, "Write", new { file_path = path, content = "x" });

        allowed.Should().BeFalse(path);
        blocked.Should().ContainSingle();
    }

    [Theory]
    [InlineData(@"docs\NOTES.md")]
    [InlineData(@"C:\dev\repo-worktrees\WAV-27\src\a.ts")]
    [InlineData(@"c:/dev/repo-worktrees/WAV-27/src/a.ts")]          // lower-case drive, forward slashes
    [InlineData(@"C:\dev\repo-worktrees\WAV-27\src\..\README.md")]
    public async Task Windows_InsideTheWorktree_IsAllowed(string path)
    {
        const string worktree = @"C:\dev\repo-worktrees\WAV-27";
        var (allowed, _, _) = await Run(worktree, worktree, "MultiEdit", new { file_path = path, edits = Array.Empty<object>() }, ignoreCase: true);

        allowed.Should().BeTrue(path);
    }

    [Theory]
    [InlineData(@"C:\dev\repo-worktrees\WAV-28\src\a.ts")]
    [InlineData(@"C:\dev\repo\src\a.ts")]
    [InlineData(@"D:\dev\repo-worktrees\WAV-27\src\a.ts")]
    [InlineData(@"..\..\repo\src\a.ts")]
    public async Task Windows_OutsideTheWorktree_IsDenied(string path)
    {
        const string worktree = @"C:\dev\repo-worktrees\WAV-27";
        var (allowed, _, _) = await Run(worktree, worktree, "Write", new { file_path = path, content = "x" }, ignoreCase: true);

        allowed.Should().BeFalse(path);
    }

    [Fact]
    public async Task SandboxCwdThatDiffersFromTheRequestWorktree_IsAlsoAnAllowedRoot()
    {
        // A container mounts the worktree at a different path than the host knows; the CLI's
        // cwd is the truth for the agent, the request's path is the truth for the host. Both
        // name the same tree, so writes under either are fine.
        var (allowed, _, _) = await Run(
            worktree: "/srv/repos/org/repo/worktrees/T-1", cwd: "/workspace",
            "Write", new { file_path = "/workspace/src/a.cs", content = "x" });
        allowed.Should().BeTrue();

        var (allowedHost, _, _) = await Run(
            worktree: "/srv/repos/org/repo/worktrees/T-1", cwd: "/workspace",
            "Write", new { file_path = "/srv/repos/org/repo/worktrees/T-1/src/a.cs", content = "x" });
        allowedHost.Should().BeTrue();

        var (denied, _, _) = await Run(
            worktree: "/srv/repos/org/repo/worktrees/T-1", cwd: "/workspace",
            "Write", new { file_path = "/workspace-other/src/a.cs", content = "x" });
        denied.Should().BeFalse();
    }

    [Fact]
    public async Task NotebookEdit_UsesNotebookPath()
    {
        const string worktree = "/repos/org/repo/worktrees/T-1";
        var (allowed, _, _) = await Run(worktree, worktree, "NotebookEdit", new { notebook_path = "/tmp/x.ipynb", new_source = "" });
        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task NoPathInTheInput_IsAllowed()
    {
        // Not this guard's call — an unfamiliar shape must never block the run.
        const string worktree = "/repos/org/repo/worktrees/T-1";
        var (allowed, _, _) = await Run(worktree, worktree, "Write", new { content = "x" });
        allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\a\b\..\c\", "C:/a/c")]
    [InlineData("/a/./b//c/", "/a/b/c")]
    [InlineData(@"\\server\share\dir\..\x", "//server/share/x")]
    [InlineData("d:/x", "D:/x")]
    public void Normalize_IsOsAgnostic(string input, string expected)
    {
        AgentWorktreeBoundaryGuard.Normalize(input).Should().Be(expected);
    }
}
