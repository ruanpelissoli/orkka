namespace Orkka.Local;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

/// <summary>
/// Serves the built web UI (a static single-page app in <see cref="LocalSettings.UiDir"/>) on
/// every path the host does not own itself (<see cref="LocalPaths"/>), to the contract in
/// <c>ui/apps/local/README.md</c> → "How it is served":
/// <list type="bullet">
/// <item>a file in the build is public — it carries no data — with a long immutable cache for
/// <c>/assets/*</c> and <c>/excalidraw/fonts/*</c> (hashed) and <c>no-cache</c> otherwise; a
/// missing file under those folders is a plain 404 (a tab on an older build then reloads);</item>
/// <item><c>/</c> and any other path without a locale prefix is redirected to
/// <c>/{locale}{path}</c> (the <c>NEXT_LOCALE</c> cookie, else the best Accept-Language match,
/// else <c>en</c>);</item>
/// <item>any other GET under <c>/{locale}/…</c> is the app's <c>index.html</c> — but only for a
/// caller with a session (or the install-secret bearer). Anyone else gets the signed-out page:
/// another local process or OS user learns nothing about this machine's Orkka from it.</item>
/// </list>
/// HTML responses carry a strict Content-Security-Policy (no inline script).
/// </summary>
public static class StaticUi
{
    /// <summary>The locales the UI ships; the first segment of every page path.</summary>
    public static readonly string[] Locales = ["en", "es", "pt", "fr", "de"];

    public const string DefaultLocale = "en";
    public const string LocaleCookie = "NEXT_LOCALE";
    public const string IndexFile = "index.html";

    /// <summary>
    /// The UI's policy (ui/apps/local/README.md explains each exception): no inline or remote
    /// script; inline styles (shiki, xterm, Excalidraw); data/blob images; data fonts; the SignalR
    /// WebSockets on this port; blob/data workers (Excalidraw). Plus <c>frame-src 'self' blob:</c>:
    /// the PDF viewer shows an attachment's object URL in an iframe, which <c>default-src</c>
    /// alone would block. Nothing may frame Orkka.
    /// </summary>
    public static string ContentSecurityPolicy(int port) =>
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; font-src 'self' data:; " +
        $"connect-src 'self' ws://127.0.0.1:{port} ws://localhost:{port}; " +
        "worker-src 'self' blob: data:; frame-src 'self' blob:; object-src 'none'; " +
        "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private const string ImmutableCache = "public, max-age=31536000, immutable";

    /// <summary>Content-hashed build folders: cached forever, and a missing file there is a 404.</summary>
    private static bool IsHashedPath(PathString path) =>
        path.StartsWithSegments("/assets") || path.StartsWithSegments("/excalidraw/fonts");

    /// <summary>
    /// <c>&lt;repo root&gt;/ui/apps/local/dist</c>: the repo root is the first folder above
    /// <paramref name="startDir"/> (the app's base directory, inside the clone's build output)
    /// that holds <c>Orkka.slnx</c>. Falls back to <c>&lt;startDir&gt;/ui</c> when there is none.
    /// </summary>
    public static string DefaultUiDir(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Orkka.slnx")))
                return Path.Combine(dir.FullName, "ui", "apps", "local", "dist");
        }
        return Path.Combine(startDir, "ui");
    }

    public static IApplicationBuilder UseStaticUi(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<LocalSettings>();
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".woff2"] = "font/woff2";
        contentTypes.Mappings[".woff"] = "font/woff";
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
        contentTypes.Mappings[".svg"] = "image/svg+xml";
        contentTypes.Mappings[".mjs"] = "text/javascript";
        contentTypes.Mappings[".map"] = "application/json";

        PhysicalFileProvider? files = null;
        StaticFileMiddleware? staticFiles = null;
        var gate = new Lock();

        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (LocalPaths.IsHostOwned(request.Path))
            {
                await next(context);
                return;
            }

            var indexPath = Path.Combine(settings.UiDir, IndexFile);
            if (!File.Exists(indexPath))
            {
                if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                {
                    await WriteHtmlAsync(context, settings.Port, StatusCodes.Status503ServiceUnavailable, NotBuiltPage);
                    return;
                }
                await next(context);
                return;
            }

            // Built lazily: the build may appear after the host started (the script builds the UI
            // first, but a developer may run `npm run build` while Orkka runs).
            lock (gate)
            {
                if (files is null)
                {
                    files = new PhysicalFileProvider(settings.UiDir);
                    staticFiles = new StaticFileMiddleware(
                        _ => Task.CompletedTask,
                        app.Environment,
                        Microsoft.Extensions.Options.Options.Create(new StaticFileOptions
                        {
                            FileProvider = files,
                            ContentTypeProvider = contentTypes,
                            ServeUnknownFileTypes = false,
                            OnPrepareResponse = ctx =>
                            {
                                var headers = ctx.Context.Response.Headers;
                                headers.CacheControl = IsHashedPath(ctx.Context.Request.Path)
                                    ? ImmutableCache
                                    : "no-cache";
                                headers.XContentTypeOptions = "nosniff";
                            },
                        }),
                        app.Services.GetRequiredService<ILoggerFactory>());
                }
            }

            var isRead = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
            if (!isRead)
            {
                await next(context);
                return;
            }

            // A file of the build (never index.html itself: that is a page, and pages are gated).
            if (IsBuildFile(files!, request.Path))
            {
                await staticFiles!.Invoke(context);
                return;
            }

            // A chunk of another build: a plain 404, never the app (the tab then reloads).
            if (IsHashedPath(request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
            var first = segments.FirstOrDefault();
            if (first is null || !Locales.Contains(first, StringComparer.Ordinal))
            {
                // `/` → `/{locale}`, `/x/y?q` → `/{locale}/x/y?q`.
                var path = request.Path.Value is { Length: > 1 } p ? p : "";
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Redirect($"/{PickLocale(request)}{path}{request.QueryString}");
                return;
            }

            // A page. No page is served without a session (or the install-secret bearer).
            var auth = await context.AuthenticateAsync(Auth.LocalAuthHandler.SchemeName);
            if (!auth.Succeeded)
            {
                await WriteHtmlAsync(context, settings.Port, StatusCodes.Status401Unauthorized, SignedOutPage);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            ApplyHtmlHeaders(context.Response, settings.Port, "no-cache");
            if (HttpMethods.IsHead(request.Method))
                return;
            await context.Response.SendFileAsync(indexPath, context.RequestAborted);
        });
    }

    /// <summary>The <c>NEXT_LOCALE</c> cookie when it names a shipped locale, else the
    /// highest-quality Accept-Language entry whose primary tag is one, else <c>en</c>.</summary>
    public static string PickLocale(HttpRequest request)
    {
        if (request.Cookies.TryGetValue(LocaleCookie, out var cookie)
            && Locales.Contains(cookie, StringComparer.Ordinal))
            return cookie!;

        try
        {
            var languages = request.GetTypedHeaders().AcceptLanguage;
            foreach (var language in languages.OrderByDescending(l => l.Quality ?? 1))
            {
                if (language.Quality is <= 0)
                    continue;
                var tag = language.Value.Value ?? "";
                var primary = tag.Split('-', 2)[0].ToLowerInvariant();
                if (Locales.Contains(primary, StringComparer.Ordinal))
                    return primary;
            }
        }
        catch (FormatException)
        {
            // A malformed header picks the default.
        }
        return DefaultLocale;
    }

    private static bool IsBuildFile(IFileProvider files, PathString path)
    {
        if (!path.HasValue || path.Value == "/" || path.Value.EndsWith('/'))
            return false;
        if (string.Equals(Path.GetFileName(path.Value), IndexFile, StringComparison.OrdinalIgnoreCase))
            return false;
        // PhysicalFileProvider refuses anything that escapes its root (.., absolute paths).
        var file = files.GetFileInfo(path.Value);
        return file.Exists && !file.IsDirectory;
    }

    private static void ApplyHtmlHeaders(HttpResponse response, int port, string cache)
    {
        response.Headers.CacheControl = cache;
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy(port);
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XFrameOptions = "DENY";
    }

    private static Task WriteHtmlAsync(HttpContext context, int port, int status, string html, string cache = "no-store")
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        ApplyHtmlHeaders(context.Response, port, cache);
        return HttpMethods.IsHead(context.Request.Method)
            ? Task.CompletedTask
            : context.Response.WriteAsync(html, context.RequestAborted);
    }

    // No inline script anywhere (the CSP forbids it); one inline <style> is allowed.
    private const string PageStyle =
        "<style>body{font-family:system-ui,sans-serif;max-width:34rem;margin:15vh auto;padding:0 1rem;color:#222}"
        + "code{background:#f2f2f2;padding:.1rem .3rem;border-radius:4px}</style>";

    public const string SignedOutPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Orkka</title>
        """ + PageStyle + """
        </head>
        <body><h1>Open Orkka from your terminal</h1>
        <p>This browser has no Orkka session. In your Orkka folder, run <code>./orkka open</code>
        (Windows: <code>orkka open</code>) to sign this browser in.</p>
        </body></html>
        """;

    public const string NotBuiltPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Orkka</title>
        """ + PageStyle + """
        </head>
        <body><h1>The UI isn't built</h1>
        <p>Run <code>./orkka start</code> (it builds it), or <code>npm ci &amp;&amp; npm run build</code> in <code>ui/</code>.</p>
        </body></html>
        """;
}
