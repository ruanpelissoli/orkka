namespace Orkka.Local.Storage;

using Microsoft.AspNetCore.StaticFiles;

/// <summary>
/// Serves and accepts blobs written by <see cref="FileSystemBlobStorageService"/>:
/// <list type="bullet">
/// <item><c>GET /blobs/{container}/{**blob}</c> — a valid signed GET URL, or a request the
/// local user is authenticated for (session cookie or install secret);</item>
/// <item><c>PUT /blobs/{container}/{**blob}</c> — a valid signed PUT URL only (the browser's
/// direct upload, mirroring an Azure SAS upload: raw bytes, not a multipart form).</item>
/// </list>
/// </summary>
public static class LocalBlobEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapLocalBlobs(this WebApplication app)
    {
        app.MapMethods("/blobs/{container}/{**blob}", [HttpMethods.Get, HttpMethods.Head], (
            HttpContext http, string container, string blob,
            FileSystemBlobStorageService storage, BlobUrlSigner signer) =>
        {
            var signed = signer.Verify(HttpMethods.Get, container, blob,
                http.Request.Query["exp"], http.Request.Query["sig"]);
            if (!signed && http.User.Identity?.IsAuthenticated != true)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            string path;
            try { path = storage.GetPath(container, blob); }
            catch (InvalidOperationException) { return Results.NotFound(); }
            if (!File.Exists(path))
                return Results.NotFound();

            // The real MIME type matters: browsers never content-sniff SVG, so design assets
            // render as broken images under octet-stream.
            if (!ContentTypes.TryGetContentType(path, out var contentType))
                contentType = "application/octet-stream";

            // ?filename= makes the save dialog show the asset's name instead of the id-based
            // blob name; without it the file renders inline.
            var downloadFileName = http.Request.Query["filename"].ToString();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(path, contentType,
                string.IsNullOrWhiteSpace(downloadFileName) ? null : downloadFileName);
        }).AllowAnonymous();

        app.MapPut("/blobs/{container}/{**blob}", async (
            HttpContext http, string container, string blob,
            FileSystemBlobStorageService storage, BlobUrlSigner signer, CancellationToken ct) =>
        {
            if (!signer.Verify(HttpMethods.Put, container, blob,
                    http.Request.Query["exp"], http.Request.Query["sig"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                await storage.UploadAsync(container, blob, http.Request.Body,
                    http.Request.ContentType ?? "application/octet-stream", ct);
            }
            catch (InvalidOperationException)
            {
                return Results.BadRequest();
            }
            return Results.NoContent();
        }).AllowAnonymous();
    }
}
