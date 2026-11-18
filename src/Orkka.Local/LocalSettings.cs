namespace Orkka.Local;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The host's own settings, read once at startup.
/// </summary>
/// <param name="Port">Where Kestrel listens, on 127.0.0.1 only (<c>Local:Port</c>, default 6750).
/// The only origin the browser talks to.</param>
/// <param name="DataDir">The database (<c>orkka.db</c>), blobs and sessions
/// (<c>Local:DataDir</c>, default <c>%ORKKA_HOME%/data</c> or <c>~/.orkka/data</c>).</param>
/// <param name="UiDir">The built web UI the host serves (<c>Local:UiDir</c>, default
/// <c>&lt;repo&gt;/ui/apps/local/dist</c>, see <see cref="StaticUi.DefaultUiDir"/>).</param>
/// <param name="InstallSecret">The install secret (<c>Local:InstallSecret</c>, handed over in
/// memory by <c>./orkka start</c>, never through the environment): the bearer the CLI presents.
/// Empty = no bearer is accepted.</param>
public sealed record LocalSettings(
    int Port,
    string DataDir,
    string UiDir,
    string InstallSecret)
{
    public const int DefaultPort = 6750;

    public const string InstallSecretKey = "Local:InstallSecret";

    /// <summary>The SQLite database every module shares, unless <c>ConnectionStrings:DefaultConnection</c> names another.</summary>
    public string DatabasePath => Path.Combine(DataDir, Orkka.SharedKernel.Persistence.SqliteDatabase.FileName);

    /// <summary>The loopback origin the CLI and the in-process runner use.</summary>
    public string LoopbackBaseUrl => $"http://127.0.0.1:{Port}";

    public static LocalSettings From(IConfiguration configuration)
    {
        var port = configuration.GetValue("Local:Port", DefaultPort);
        var dataDir = configuration["Local:DataDir"];
        if (string.IsNullOrWhiteSpace(dataDir))
            dataDir = Path.Combine(OrkkaHome(), "data");
        var uiDir = configuration["Local:UiDir"];
        if (string.IsNullOrWhiteSpace(uiDir))
            uiDir = StaticUi.DefaultUiDir(AppContext.BaseDirectory);

        return new LocalSettings(
            port,
            Path.GetFullPath(dataDir),
            Path.GetFullPath(uiDir),
            configuration[InstallSecretKey] ?? "");
    }

    /// <summary><c>ORKKA_HOME</c>, else <c>~/.orkka</c> — the same root the runner uses.</summary>
    public static string OrkkaHome()
    {
        var home = Environment.GetEnvironmentVariable("ORKKA_HOME");
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orkka")
            : Path.GetFullPath(home);
    }

    /// <summary>Constant-time check of a presented bearer against the install secret.</summary>
    public bool IsInstallSecret(string? presented)
    {
        if (string.IsNullOrEmpty(InstallSecret) || string.IsNullOrEmpty(presented))
            return false;
        // Hash both sides so the comparison is fixed-length whatever was presented.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(InstallSecret)));
    }

    public override string ToString() => $"LocalSettings {{ Port = {Port}, DataDir = {DataDir}, UiDir = {UiDir} }}";
}
