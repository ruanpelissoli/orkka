namespace Orkka.LocalRunner.Trackers;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

/// <summary>
/// Where a workspace's secrets live on this machine: its tracker tokens (keyed by tracker
/// connection id) and its Orkka runner token (<see cref="WorkspaceSecretStores.RunnerTokenId"/>).
/// Each implementation is bound to one workspace profile and namespaces the id itself. A secret
/// never leaves this machine except the runner token, and that only as the bearer credential of
/// the runner's own connection — never into a log, an error, or an agent's environment.
/// </summary>
public interface IWorkspaceSecretStore
{
    /// <summary>Human words for where the secrets go ("Windows Credential Manager").</summary>
    string Description { get; }

    /// <summary>The secret, or null when none is stored under <paramref name="id"/>.</summary>
    string? Get(string id);

    void Set(string id, string secret);

    /// <summary>Removes the secret; a missing one is not an error.</summary>
    void Delete(string id);
}

/// <summary>Picks the best secret store this machine has, at runtime.</summary>
public static class WorkspaceSecretStores
{
    /// <summary><c>ORKKA_SECRET_STORE=file</c> forces the owner-only file store.</summary>
    public const string StoreOverrideVariable = "ORKKA_SECRET_STORE";

    // The runner deletes every variable whose name looks sensitive ("SECRET") from its own
    // environment at startup, so it can never reach an agent — ORKKA_SECRET_STORE included.
    // `connect` captures it first; without that, a runner started with ORKKA_SECRET_STORE=file
    // (a sandbox, a machine whose keychain must not be used) silently wrote tracker tokens into
    // the OS keychain anyway.
    private static string? _capturedOverride;

    /// <summary>Remembers <c>ORKKA_SECRET_STORE</c> before the runner's environment scrub.</summary>
    public static void CaptureOverride(Func<string, string?>? getEnvironment = null)
        => _capturedOverride = (getEnvironment ?? Environment.GetEnvironmentVariable)(StoreOverrideVariable);

    /// <summary>True when <c>ORKKA_SECRET_STORE=file</c> is set now or was when captured.</summary>
    internal static bool FileStoreForced()
        => string.Equals(Environment.GetEnvironmentVariable(StoreOverrideVariable) ?? _capturedOverride, "file",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The OS store when there is one; otherwise the 0600 file store under the
    /// profile's workspace folder, with a one-time warning — a headless Linux box without a
    /// D-Bus session still has to be able to hold a token.</summary>
    public static IWorkspaceSecretStore CreateDefault(string orkkaRoot, string profile)
    {
        // Test harnesses (ORKKA_HOME sandboxes) must not write into the real OS keychain, and
        // neither may anything pointed at a root other than this machine's Orkka home (unit
        // tests' temp folders): keychain entries are per user, not per folder, so they would
        // leak between sandboxes and outlive them.
        if (FileStoreForced() || !SameDirectory(orkkaRoot, Cli.RunnerEnvFile.OrkkaDir))
            return new FileSecretStore(TrackerPaths.SecretsDir(orkkaRoot, profile));

        if (OperatingSystem.IsWindows())
            return new WindowsCredentialSecretStore(profile);
        if (OperatingSystem.IsMacOS() && File.Exists(MacKeychainSecretStore.SecurityPath))
            return new MacKeychainSecretStore(profile);
        if (OperatingSystem.IsLinux() && LibsecretSecretStore.IsAvailable())
            return new LibsecretSecretStore(profile);

        var store = new FileSecretStore(TrackerPaths.SecretsDir(orkkaRoot, profile));
        FileSecretStore.WarnOnce(store.Directory);
        return store;
    }

    private static bool SameDirectory(string a, string b) => string.Equals(
        Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The id the workspace's Orkka runner token is stored under. Tracker ids are
    /// <c>{kind}-{site}</c> slugs (<c>jira-…</c>, <c>ado-…</c>), so they never collide with it.</summary>
    public const string RunnerTokenId = "runner-token";

    /// <summary>The scope the local stack's own secrets live under (the
    /// install secret, the proxy secret, the data-encryption key — see
    /// <c>Runtime.LocalSecrets</c>). Not a valid workspace name, so it can never collide with one,
    /// and the file fallback's folder is skipped when workspaces are listed.</summary>
    public const string LocalStackScope = "_local";

    /// <summary>The OS-level name every store uses for one secret.</summary>
    public static string KeyFor(string profile, string id) => profile == LocalStackScope
        ? $"orkka/local/{id}"
        : id == RunnerTokenId
            ? $"orkka/{profile}/{RunnerTokenId}"
            : WorkspaceProviderEnv.IsSecretId(id)
                ? $"orkka/{profile}/ai/{WorkspaceProviderEnv.NameOfSecretId(id)}"
                : $"orkka/{profile}/tracker/{id}";

    /// <summary>What a person sees for the secret in their keychain UI.</summary>
    public static string LabelFor(string id) => id == RunnerTokenId
        ? "Orkka runner token"
        : WorkspaceProviderEnv.IsSecretId(id)
            ? $"Orkka AI sign-in {WorkspaceProviderEnv.NameOfSecretId(id)}"
            : $"Orkka tracker {id}";

    /// <summary>The keychain label for a secret in <paramref name="profile"/>.</summary>
    public static string LabelFor(string profile, string id) => profile == LocalStackScope
        ? $"Orkka local {id}"
        : LabelFor(id);
}

/// <summary>Windows Credential Manager (generic credentials, persisted per machine for this
/// user). P/Invoke rather than a package: three calls.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialSecretStore(string profile) : IWorkspaceSecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string Description => "Windows Credential Manager";

    public string? Get(string id)
    {
        if (!CredReadW(WorkspaceSecretStores.KeyFor(profile, id), CredTypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return null;
            throw new InvalidOperationException($"Windows Credential Manager could not read the secret (error {error}).");
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string id, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = WorkspaceSecretStores.KeyFor(profile, id),
                Comment = WorkspaceSecretStores.LabelFor(profile, id),
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = id,
            };
            if (!CredWriteW(ref cred, 0))
                throw new InvalidOperationException(
                    $"Windows Credential Manager could not store the secret (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            // The blob held the token in unmanaged memory — wipe it before handing it back.
            for (var i = 0; i < bytes.Length; i++)
                Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public void Delete(string id)
    {
        if (!CredDeleteW(WorkspaceSecretStores.KeyFor(profile, id), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
                throw new InvalidOperationException($"Windows Credential Manager could not delete the secret (error {error}).");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

#pragma warning disable SYSLIB1054 // DllImport keeps the struct-with-strings marshalling simple; no AllowUnsafeBlocks needed.
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
#pragma warning restore SYSLIB1054
}

/// <summary>macOS login keychain through the <c>security</c> CLI (service "orkka", account =
/// the namespaced key).</summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainSecretStore(string profile) : IWorkspaceSecretStore
{
    internal const string SecurityPath = "/usr/bin/security";
    private const int ItemNotFound = 44;

    public string Description => "macOS Keychain";

    public string? Get(string id)
    {
        var (code, stdout, _) = SecretCli.Run(SecurityPath,
            ["find-generic-password", "-s", "orkka", "-a", WorkspaceSecretStores.KeyFor(profile, id), "-w"]);
        if (code == ItemNotFound)
            return null;
        if (code != 0)
            throw new InvalidOperationException($"The macOS Keychain could not read the secret (security exit {code}).");
        return stdout.TrimEnd('\n', '\r');
    }

    public void Set(string id, string secret)
    {
        var key = WorkspaceSecretStores.KeyFor(profile, id);
        // `security add-generic-password -w` with no value prompts on the TTY, not stdin, so
        // the token cannot be piped to it. Interactive mode (`security -i`) reads whole
        // commands from stdin instead, which keeps the token out of argv (visible to every
        // process via `ps`). Its tokenizer has no escape for a quote or backslash, so a token
        // containing one falls back to argv — Jira and ADO tokens never do.
        if (secret.IndexOfAny(['"', '\\', '\n', '\r']) < 0)
        {
            var (code, _, _) = SecretCli.Run(SecurityPath, ["-i"],
                stdin: $"add-generic-password -U -s orkka -a \"{key}\" -l \"{WorkspaceSecretStores.LabelFor(profile, id)}\" -w \"{secret}\"\n");
            if (code == 0 && Get(id) == secret)
                return;
        }

        // Accepted exposure: the token is in this short-lived process's argv for the
        // duration of the call.
        var (exit, _, _) = SecretCli.Run(SecurityPath,
            ["add-generic-password", "-U", "-s", "orkka", "-a", key, "-l", WorkspaceSecretStores.LabelFor(profile, id), "-w", secret]);
        if (exit != 0)
            throw new InvalidOperationException($"The macOS Keychain could not store the secret (security exit {exit}).");
    }

    public void Delete(string id)
    {
        var (code, _, _) = SecretCli.Run(SecurityPath,
            ["delete-generic-password", "-s", "orkka", "-a", WorkspaceSecretStores.KeyFor(profile, id)]);
        if (code != 0 && code != ItemNotFound)
            throw new InvalidOperationException($"The macOS Keychain could not delete the secret (security exit {code}).");
    }
}

/// <summary>Linux Secret Service (GNOME Keyring, KWallet) through libsecret's
/// <c>secret-tool</c>, which reads the secret on stdin.</summary>
public sealed class LibsecretSecretStore(string profile) : IWorkspaceSecretStore
{
    public string Description => "the Secret Service keyring (secret-tool)";

    /// <summary>secret-tool installed AND a keyring reachable: a lookup of a key that does
    /// not exist exits 1 silently, while a missing D-Bus session exits non-zero with an
    /// error on stderr.</summary>
    public static bool IsAvailable()
    {
        try
        {
            var (code, _, stderr) = SecretCli.Run("secret-tool",
                ["lookup", "service", "orkka", "account", "orkka/__probe__"], timeoutMs: 5000);
            return code == 0 || (code == 1 && string.IsNullOrWhiteSpace(stderr));
        }
        catch
        {
            return false;
        }
    }

    public string? Get(string id)
    {
        var (code, stdout, stderr) = SecretCli.Run("secret-tool",
            ["lookup", "service", "orkka", "account", WorkspaceSecretStores.KeyFor(profile, id)]);
        if (code == 0)
            return stdout.TrimEnd('\n', '\r');
        if (code == 1 && string.IsNullOrWhiteSpace(stderr))
            return null;
        throw new InvalidOperationException($"secret-tool could not read the secret (exit {code}).");
    }

    public void Set(string id, string secret)
    {
        var (code, _, _) = SecretCli.Run("secret-tool",
            ["store", $"--label={WorkspaceSecretStores.LabelFor(profile, id)}", "service", "orkka", "account", WorkspaceSecretStores.KeyFor(profile, id)],
            stdin: secret);
        if (code != 0)
            throw new InvalidOperationException($"secret-tool could not store the secret (exit {code}).");
    }

    public void Delete(string id)
        // clear exits non-zero when nothing matched; deleting a missing secret is fine.
        => SecretCli.Run("secret-tool", ["clear", "service", "orkka", "account", WorkspaceSecretStores.KeyFor(profile, id)]);
}

/// <summary>
/// Last resort when the machine has no OS secret store: one file per secret under
/// <c>~/.orkka/workspaces/{profile}/secrets/</c>, owner-only (0600, directory 0700) on Unix.
/// Weaker than a keyring — anything running as this user can read it — hence the warning.
/// </summary>
public sealed class FileSecretStore(string directory) : IWorkspaceSecretStore
{
    private static int _warned;

    public string Directory { get; } = directory;

    public string Description => $"a private file under {Directory}";

    internal static void WarnOnce(string directory)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            Console.Error.WriteLine(
                $"Warning: no OS secret store is available on this machine, so Orkka's tokens are kept in owner-only files under {directory}.");
        }
    }

    public string? Get(string id)
    {
        var path = PathFor(id);
        return File.Exists(path) ? File.ReadAllText(path).TrimEnd('\n', '\r') : null;
    }

    public void Set(string id, string secret)
    {
        EnsureDirectory();
        var path = PathFor(id);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        // The mode is applied at creation, so the token is never readable by others, not
        // even for the instant between create and chmod.
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(secret);
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path))
            File.Delete(path);
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(Directory);
            return;
        }
        System.IO.Directory.CreateDirectory(Directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string PathFor(string id)
    {
        // Ids are slugs, but a hand-edited trackers.json must not turn into a path escape.
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || id.Contains("..", StringComparison.Ordinal) || id.Contains('/') || id.Contains('\\'))
            throw new ArgumentException($"'{id}' is not a valid secret id.", nameof(id));
        return Path.Combine(Directory, id);
    }
}

/// <summary>Runs a secret-store CLI without a shell. Output is returned, never logged.</summary>
internal static class SecretCli
{
    public static (int ExitCode, string Stdout, string Stderr) Run(
        string fileName, IReadOnlyList<string> args, string? stdin = null, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (stdin is not null)
            process.StandardInput.Write(stdin);
        process.StandardInput.Close();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} did not answer in time.");
        }
        process.WaitForExit();
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }
}
