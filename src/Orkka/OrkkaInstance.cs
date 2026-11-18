namespace Orkka.Cli;

using System.Diagnostics;
using System.Text.Json;

/// <summary>Where the running instance is: written by <c>start</c>, read by <c>open</c>,
/// <c>status</c> and <c>stop</c>.</summary>
/// <param name="ProcessStartedAt">The process's own start time, so a PID the OS reused for
/// another program is never mistaken for Orkka (and never stopped).</param>
public sealed record InstanceInfo(int Pid, int Port, DateTimeOffset StartedAt, DateTimeOffset ProcessStartedAt)
{
    public string Url => $"http://127.0.0.1:{Port}";
}

/// <summary>
/// One Orkka per Orkka home (<c>~/.orkka</c> or <c>ORKKA_HOME</c>): <c>run/orkka.lock</c> is held
/// exclusively by the running <c>start</c> (the OS releases it however the process ends), and
/// <c>run/instance.json</c> says which PID and port it is.
/// </summary>
public static class OrkkaInstance
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string RunDir(string home) => Path.Combine(home, "run");
    public static string LockFile(string home) => Path.Combine(RunDir(home), "orkka.lock");
    public static string InfoFile(string home) => Path.Combine(RunDir(home), "instance.json");
    public static string LogsDir(string home) => Path.Combine(home, "logs");
    public static string LogFile(string home) => Path.Combine(LogsDir(home), "orkka.log");

    /// <summary>The exclusive lock, or null when another Orkka holds it.</summary>
    public static FileStream? TryLock(string home)
    {
        Directory.CreateDirectory(RunDir(home));
        try
        {
            return new FileStream(LockFile(home), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string home, InstanceInfo info)
    {
        Directory.CreateDirectory(RunDir(home));
        var tmp = InfoFile(home) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(info, Json));
        File.Move(tmp, InfoFile(home), overwrite: true);
    }

    public static void Delete(string home)
    {
        try { File.Delete(InfoFile(home)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The recorded instance, or null when there is none or the file is unreadable.</summary>
    public static InstanceInfo? Read(string home)
    {
        try
        {
            var path = InfoFile(home);
            return File.Exists(path) ? JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The recorded instance when its process is still the one that wrote it.</summary>
    public static InstanceInfo? ReadAlive(string home) =>
        Read(home) is { } info && FindProcess(info) is not null ? info : null;

    /// <summary>The process behind <paramref name="info"/>, only when its PID still belongs to it.</summary>
    public static Process? FindProcess(InstanceInfo info)
    {
        try
        {
            var process = Process.GetProcessById(info.Pid);
            if (process.HasExited)
                return null;
            var started = StartTimeOf(process);
            return started is null || Math.Abs((started.Value - info.ProcessStartedAt).TotalSeconds) < 2
                ? process
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static DateTimeOffset? StartTimeOf(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
