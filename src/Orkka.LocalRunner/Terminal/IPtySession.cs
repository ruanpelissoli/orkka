namespace Orkka.LocalRunner.Terminal;

/// <summary>Spawn parameters for one pseudo-terminal shell. Environment is the FULL
/// variable set for the child (not overrides) — the terminal is the user's own shell on
/// their own machine, so unlike agent runs nothing is sanitized away beyond what the
/// runner process itself scrubbed at startup.</summary>
internal sealed record PtyOptions(
    string ShellPath,
    string[] Args,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int Cols,
    int Rows);

/// <summary>
/// One live pseudo-terminal (ConPTY on Windows, forkpty on Unix). Output is the raw VT
/// byte stream the shell renders — never re-encode it; UTF-8 sequences may split across
/// reads and only the browser-side terminal can reassemble them.
/// </summary>
internal interface IPtySession : IDisposable
{
    /// <summary>Writes keystroke/paste bytes to the shell's input.</summary>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    void Resize(int cols, int rows);

    /// <summary>Reads raw VT output. Returns 0 at end-of-stream (the shell exited and
    /// buffered output has been drained). Blocks until data is available.
    ///
    /// Killing the session is what normally ends the stream, but callers must NOT treat
    /// that as guaranteed: on Windows a read already pending when the pseudo-terminal is
    /// disposed can stay blocked indefinitely. Anything that must be cancellable has to
    /// race this call against its own token (see AppRunManager).</summary>
    Task<int> ReadOutputAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>Completes with the shell's exit code once it terminates.</summary>
    Task<int> WaitForExitAsync(CancellationToken ct);

    /// <summary>Kills the shell and its whole process tree. Idempotent.</summary>
    void Kill();
}

internal static class Pty
{
    public static IPtySession Spawn(PtyOptions options) => OperatingSystem.IsWindows()
        ? new WindowsPtySession(options)
        : new UnixPtySession(options);
}
