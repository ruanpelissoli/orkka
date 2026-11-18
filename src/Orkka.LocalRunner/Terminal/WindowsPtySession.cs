namespace Orkka.LocalRunner.Terminal;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// ConPTY-backed pseudo-terminal (Windows 10 1809+). Two anonymous pipes bridge the
/// pseudoconsole and the shell is spawned with PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE so it
/// believes it owns a real console. Close order is load-bearing: the output pipe only
/// EOFs after ClosePseudoConsole, and ClosePseudoConsole can block unless the pipe is
/// being drained — so the pseudoconsole is closed from the exit watcher WHILE the read
/// loop keeps running, never the other way around.
/// </summary>
internal sealed class WindowsPtySession : IPtySession
{
    private readonly IntPtr _hpc;
    private readonly SafeFileHandle _ptyInputRead;
    private readonly IntPtr _processHandle;
    private readonly int _pid;
    private readonly FileStream _output;
    private readonly FileStream _input;
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEvent _processExitEvent;
    private readonly RegisteredWaitHandle _exitWait;
    private int _disposed;

    public WindowsPtySession(PtyOptions options)
    {
        // Pipe pair 1: we write keystrokes → ConPTY reads. Pipe pair 2: ConPTY writes
        // rendered VT → we read.
        if (!CreatePipe(out var ptyInputRead, out var inputWrite, IntPtr.Zero, 0))
            throw new Win32Exception();
        if (!CreatePipe(out var outputRead, out var ptyOutputWrite, IntPtr.Zero, 0))
            throw new Win32Exception();

        var size = new COORD { X = (short)options.Cols, Y = (short)options.Rows };
        var hr = CreatePseudoConsole(size, ptyInputRead, ptyOutputWrite, 0, out _hpc);
        if (hr != 0)
            throw new Win32Exception(hr, "CreatePseudoConsole failed");

        // Close OUR copy of the conpty output end so the read pipe EOFs when conhost
        // shuts down — but keep the input end alive for the whole session: dropping it
        // EOFs the shell's console input and every shell then exits cleanly within ~1s.
        _ptyInputRead = ptyInputRead;
        ptyOutputWrite.Dispose();

        var attrListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        var attrList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrList, 0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            // STARTF_USESTDHANDLES with null handles is load-bearing: when the runner
            // itself has redirected std handles (detached start, logs piped to a file),
            // CreateProcessW silently duplicates them into the child even with
            // bInheritHandles=false — and the shell then writes its output to our log
            // file instead of the pseudoconsole. Nulling them forces the child onto the
            // ConPTY handles (same trick Windows Terminal's ConptyConnection uses).
            var startupInfo = new STARTUPINFOEXW
            {
                StartupInfo = new STARTUPINFOW
                {
                    cb = Marshal.SizeOf<STARTUPINFOEXW>(),
                    dwFlags = STARTF_USESTDHANDLES,
                },
                lpAttributeList = attrList,
            };
            var commandLine = new StringBuilder(BuildCommandLine(options.ShellPath, options.Args));
            var envBlock = BuildEnvironmentBlock(options.Environment);
            try
            {
                if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                        envBlock, options.WorkingDirectory, ref startupInfo, out var pi))
                    throw new Win32Exception();
                _processHandle = pi.hProcess;
                _pid = pi.dwProcessId;
                CloseHandle(pi.hThread);
            }
            finally
            {
                Marshal.FreeHGlobal(envBlock);
            }
        }
        catch
        {
            ClosePseudoConsole(_hpc);
            ptyInputRead.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }

        _output = new FileStream(outputRead, FileAccess.Read, 16 * 1024, isAsync: false);
        _input = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false);

        // Exit watcher: record the code, then close the pseudoconsole so the (still
        // draining) read loop sees the tail output followed by EOF. The grace delay is
        // load-bearing: conhost renders asynchronously, and closing it the instant the
        // shell exits discards frames it hasn't piped yet (a fast `echo x && exit`
        // loses the echo). Modern conhost EOFs the pipe itself once the last client
        // exits, so the delayed close is usually pure cleanup.
        _processExitEvent = new ManualResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(_processHandle, ownsHandle: false),
        };
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_processExitEvent, (_, _) =>
        {
            GetExitCodeProcess(_processHandle, out var code);
            _exit.TrySetResult(code);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                ClosePseudoConsole(_hpc);
            });
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _input.WriteAsync(data, ct);
        await _input.FlushAsync(ct);
    }

    public void Resize(int cols, int rows)
    {
        if (_exit.Task.IsCompleted)
            return;
        ResizePseudoConsole(_hpc, new COORD { X = (short)cols, Y = (short)rows });
    }

    public async Task<int> ReadOutputAsync(Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            return await _output.ReadAsync(buffer, ct);
        }
        catch (IOException)
        {
            return 0; // broken pipe after ClosePseudoConsole — that IS end-of-stream
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    public Task<int> WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

    public void Kill()
    {
        if (_exit.Task.IsCompleted)
            return;
        try
        {
            // Same tree-kill CliAgentInvokerBase uses: the shell may have spawned
            // children (npm, dotnet watch) that must not outlive the session.
            using var killer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/T /F /PID {_pid}",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            killer?.WaitForExit(5000);
        }
        catch
        {
            try { TerminateProcess(_processHandle, 1); }
            catch { /* already gone */ }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        Kill();
        // The exit watcher owns pseudoconsole shutdown; readers observe EOF and unwind.
        _exitWait.Unregister(null);
        try { _input.Dispose(); }
        catch { /* pipe already broken */ }
        try { _output.Dispose(); }
        catch { /* pipe already broken */ }
        _ptyInputRead.Dispose();
        _processExitEvent.Dispose();
        CloseHandle(_processHandle);
    }

    private static string BuildCommandLine(string shellPath, string[] args)
        => args.Length == 0 ? $"\"{shellPath}\"" : $"\"{shellPath}\" {string.Join(' ', args)}";

    /// <summary>Unicode environment block: "K=V\0K=V\0\0", keys sorted case-insensitively
    /// as CreateProcessW expects.</summary>
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> env)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in env.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            builder.Append(key).Append('=').Append(value).Append('\0');
        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    // ── Win32 interop ────────────────────────────────────────────────────────

    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEXW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
