namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Git;
using Info = Orkka.Modules.Execution.Git.WorktreeProcessSweeper.ProcessInfo;

/// <summary>
/// Which processes a finished task's worktree cleanup may end on the user's machine. Prod
/// runner, 2026-09-17: every merged storefront-ui worktree survived cleanup because something
/// the run left behind held files in it. The rule is narrow — this is a person's own computer.
/// </summary>
public sealed class WorktreeProcessSweeperTests
{
    private const string Worktree = @"C:\Code\storefront\storefront-ui-worktrees\STO-3";
    private const int Self = 4242;

    [Fact]
    public void AnExecutableInsideTheWorktree_IsEnded()
        => WorktreeProcessSweeper.ShouldEnd(new Info(900, "esbuild.exe",
                Worktree + @"\node_modules\@esbuild\win32-x64\esbuild.exe", "esbuild.exe --service=0.25.0 --ping"),
            Worktree, Self).Should().BeTrue();

    [Fact]
    public void ADevServerRunningFromTheWorktree_IsEnded()
        => WorktreeProcessSweeper.ShouldEnd(new Info(901, "node.exe", @"C:\Program Files\nodejs\node.exe",
                "\"C:\\Program Files\\nodejs\\node.exe\" " + Worktree + @"\node_modules\next\dist\bin\next dev"),
            Worktree, Self).Should().BeTrue();

    [Fact]
    public void ForwardSlashesAndQuotes_StillMatch()
        => WorktreeProcessSweeper.ShouldEnd(new Info(902, "node.exe", @"C:\Program Files\nodejs\node.exe",
                "node \"C:/Code/storefront/storefront-ui-worktrees/STO-3/node_modules/vitest/vitest.mjs\""),
            Worktree, Self).Should().BeTrue();

    [Fact]
    public void ADifferentTaskWhoseNameStartsTheSame_IsLeftAlone()
        => WorktreeProcessSweeper.ShouldEnd(new Info(903, "node.exe", @"C:\Program Files\nodejs\node.exe",
                "node " + Worktree + @"5\node_modules\next\dist\bin\next dev"),
            Worktree, Self).Should().BeFalse("STO-3 is not STO-35");

    [Theory]
    [InlineData("Code.exe", @"C:\Users\ruan\AppData\Local\Programs\Microsoft VS Code\Code.exe")]
    [InlineData("cmd.exe", @"C:\Windows\System32\cmd.exe")]
    [InlineData("explorer.exe", @"C:\Windows\explorer.exe")]
    public void AnEditorOrShellThatMerelyMentionsTheFolder_IsLeftAlone(string name, string exe)
        => WorktreeProcessSweeper.ShouldEnd(new Info(904, name, exe, $"\"{exe}\" \"{Worktree}\""),
            Worktree, Self).Should().BeFalse("\"Open in editor\" starts one with the worktree on its command line");

    [Fact]
    public void TheRunnerItself_IsNeverEnded()
        => WorktreeProcessSweeper.ShouldEnd(new Info(Self, "node.exe", Worktree + @"\x.exe", "node " + Worktree),
            Worktree, Self).Should().BeFalse();

    [Fact]
    public void Parse_ReadsAnArray_AndASingleObject_AndSkipsRowsWithoutAnId()
    {
        WorktreeProcessSweeper.Parse(
            "[{\"ProcessId\":10,\"Name\":\"node.exe\",\"ExecutablePath\":\"C:\\\\n.exe\",\"CommandLine\":\"node x\"},{\"Name\":\"System Idle Process\"}]")
            .Should().ContainSingle().Which.Should().Be(new Info(10, "node.exe", @"C:\n.exe", "node x"));

        WorktreeProcessSweeper.Parse("{\"ProcessId\":11,\"Name\":\"esbuild.exe\",\"ExecutablePath\":null,\"CommandLine\":null}")
            .Should().ContainSingle().Which.Should().Be(new Info(11, "esbuild.exe", null, null));

        WorktreeProcessSweeper.Parse("").Should().BeEmpty();
    }
}
