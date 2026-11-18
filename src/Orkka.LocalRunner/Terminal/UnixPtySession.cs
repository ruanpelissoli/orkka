namespace Orkka.LocalRunner.Terminal;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// forkpty-backed pseudo-terminal for macOS and Linux. The child branch between fork and
/// exec is async-signal-safe territory: every string is marshalled to native memory
/// BEFORE forkpty, all P/Invoke stubs are pre-generated (Marshal.PrelinkAll), and the
/// child calls only chdir/execve/_exit on those pre-built pointers — a single managed
/// allocation there can deadlock on a lock some other (now nonexistent) thread held.
/// </summary>
internal sealed class UnixPtySession : IPtySession
{
    private readonly int _pid;
    private readonly int _masterFd;
    private readonly FileStream _output;
    private readonly FileStream _input;
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _killed;
    private int _disposed;

    static UnixPtySession()
    {
        NativeLibrary.SetDllImportResolver(typeof(UnixPtySession).Assembly, (name, assembly, searchPath) =>
        {
            if (name != ForkptyLib)
                return IntPtr.Zero;
            // glibc keeps forkpty in libutil, musl in libc itself, macOS in libSystem.
            foreach (var candidate in new[] { "util", "libutil.so.1", "libSystem.dylib", "libc" })
            {
                if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
        Marshal.PrelinkAll(typeof(UnixPtySession));
    }

    public UnixPtySession(PtyOptions options)
    {
        // Pre-marshal everything the child needs — nothing managed may run after fork.
        var nativeStrings = new List<IntPtr>();
        IntPtr shellPtr, cwdPtr, argvPtr, envpPtr;
        try
        {
            shellPtr = MarshalUtf8(options.ShellPath, nativeStrings);
            cwdPtr = MarshalUtf8(options.WorkingDirectory, nativeStrings);
            var argv = new IntPtr[options.Args.Length + 2];
            argv[0] = shellPtr;
            for (var i = 0; i < options.Args.Length; i++)
                argv[i + 1] = MarshalUtf8(options.Args[i], nativeStrings);
            argvPtr = MarshalPtrArray(argv, nativeStrings);
            var envp = options.Environment
                .Select(e => MarshalUtf8($"{e.Key}={e.Value}", nativeStrings))
                .Append(IntPtr.Zero)
                .ToArray();
            envpPtr = MarshalPtrArray(envp, nativeStrings);
        }
        catch
        {
            FreeAll(nativeStrings);
            throw;
        }

        var winSize = new WinSize { Rows = (ushort)options.Rows, Cols = (ushort)options.Cols };
        var pid = forkpty(out var master, IntPtr.Zero, IntPtr.Zero, ref winSize);
        if (pid == -1)
        {
            FreeAll(nativeStrings);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "forkpty failed");
        }
        if (pid == 0)
        {
            // Child: pre-marshalled pointers only, straight to exec. execve only returns
            // on failure, and then the only safe exit is _exit.
            if (chdir(cwdPtr) == 0)
                execve(shellPtr, argvPtr, envpPtr);
            _exit_child(127);
        }

        // Parent
        FreeAll(nativeStrings);
        _pid = pid;
        _masterFd = master;

        var writeFd = dup(master);
        if (writeFd == -1)
        {
            close(master);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "dup failed");
        }
        _output = new FileStream(new SafeFileHandle(master, ownsHandle: true),
            FileAccess.Read, 16 * 1024, isAsync: false);
        _input = new FileStream(new SafeFileHandle(writeFd, ownsHandle: true),
            FileAccess.Write, 4096, isAsync: false);

        // Reap the child or it lingers as a zombie. Blocking a pool thread is fine at
        // the session cap (8).
        _ = Task.Run(() =>
        {
            waitpid(pid, out var status, 0);
            _exit.TrySetResult(DecodeExitStatus(status));
        });
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
        var winSize = new WinSize { Rows = (ushort)rows, Cols = (ushort)cols };
        ioctl(_masterFd, TiocSWinSz, ref winSize);
    }

    public async Task<int> ReadOutputAsync(Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            return await _output.ReadAsync(buffer, ct);
        }
        catch (IOException)
        {
            return 0; // EIO once the last slave fd closes — that IS end-of-stream
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    public Task<int> WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

    public void Kill()
    {
        if (_exit.Task.IsCompleted || Interlocked.Exchange(ref _killed, 1) == 1)
            return;
        // forkpty made the shell a session leader, so -pid signals its whole process
        // group. HUP first (shells clean up their children), KILL if it lingers.
        kill(-_pid, SIGHUP);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (!_exit.Task.IsCompleted)
                kill(-_pid, SIGKILL);
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        Kill();
        try { _input.Dispose(); }
        catch { /* fd already invalid */ }
        try { _output.Dispose(); }
        catch { /* fd already invalid */ }
    }

    private static int DecodeExitStatus(int status)
        => (status & 0x7f) == 0
            ? (status >> 8) & 0xff // WIFEXITED → WEXITSTATUS
            : 128 + (status & 0x7f); // killed by signal — shell convention

    private static IntPtr MarshalUtf8(string value, List<IntPtr> track)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        track.Add(ptr);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }

    private static IntPtr MarshalPtrArray(IntPtr[] pointers, List<IntPtr> track)
    {
        var ptr = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
        track.Add(ptr);
        Marshal.Copy(pointers, 0, ptr, pointers.Length);
        return ptr;
    }

    private static void FreeAll(List<IntPtr> pointers)
    {
        foreach (var ptr in pointers)
            Marshal.FreeHGlobal(ptr);
    }

    // ── libc/libutil interop ─────────────────────────────────────────────────

    private const string ForkptyLib = "orkka-forkpty"; // resolved in the static ctor
    private const int SIGHUP = 1;
    private const int SIGKILL = 9;

    private static readonly ulong TiocSWinSz =
        OperatingSystem.IsMacOS() ? 0x80087467UL : 0x5414UL;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort XPixel;
        public ushort YPixel;
    }

    [DllImport(ForkptyLib, SetLastError = true)]
    private static extern int forkpty(out int master, IntPtr name, IntPtr termios, ref WinSize winSize);

    [DllImport("libc")]
    private static extern int chdir(IntPtr path);

    [DllImport("libc")]
    private static extern int execve(IntPtr path, IntPtr argv, IntPtr envp);

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void _exit_child(int status);

    [DllImport("libc")]
    private static extern int kill(int pid, int sig);

    [DllImport("libc")]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc")]
    private static extern int ioctl(int fd, ulong request, ref WinSize winSize);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);

    [DllImport("libc")]
    private static extern int close(int fd);
}
