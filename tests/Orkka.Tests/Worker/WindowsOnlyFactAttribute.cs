namespace Orkka.Tests.Worker;

/// <summary>
/// A fact that runs only on Windows, and is skipped elsewhere.
/// </summary>
/// <remarks>
/// For behaviour that only EXISTS on Windows, never as a way to duck a cross-platform bug.
/// The case in point is a locked worktree: Windows refuses to delete a directory holding an
/// open file handle, which is why <c>RemoveWorktreeDirectoryAsync</c> has retry-with-backoff
/// and a tombstone rename at all. POSIX unlinks a file regardless of open handles, so the
/// scenario cannot be staged there — a test that tried would assert the opposite outcome and
/// fail on CI (ubuntu-latest) while passing on every developer's machine.
/// </remarks>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsWindows())
            Skip = $"Windows-only: {because}";
    }
}
