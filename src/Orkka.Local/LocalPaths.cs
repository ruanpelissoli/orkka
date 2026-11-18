namespace Orkka.Local;

/// <summary>
/// The path prefixes this host serves itself. Everything else is the web UI, served from the
/// built files by <see cref="StaticUi"/>.
/// </summary>
public static class LocalPaths
{
    public static readonly string[] HostOwned =
        ["/api", "/hubs", "/mcp", "/blobs", "/internal", "/launch", "/local", "/health"];

    public static bool IsHostOwned(PathString path)
    {
        foreach (var prefix in HostOwned)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
