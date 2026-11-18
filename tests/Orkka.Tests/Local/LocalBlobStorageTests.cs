namespace Orkka.Tests.Local;

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Local;
using Orkka.Local.Storage;

public sealed class LocalBlobStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orkka-blob-tests-" + Guid.NewGuid().ToString("N"));
    private readonly BlobUrlSigner _signer = new(RandomNumberGenerator.GetBytes(32));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private FileSystemBlobStorageService Storage() =>
        new(new LocalSettings(6750, _dir, "ui-dist", "s"), _signer, NullLogger<FileSystemBlobStorageService>.Instance);

    private static (string Exp, string Sig) QueryOf(string url)
    {
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        return (query["exp"]!, query["sig"]!);
    }

    [Fact]
    public async Task Upload_ThenDownload_RoundTrips_UnderTheDataDir()
    {
        var storage = Storage();
        await storage.UploadAsync("design-assets", "org/project/a.svg", new MemoryStream("<svg/>"u8.ToArray()), "image/svg+xml");

        File.Exists(Path.Combine(_dir, "blobs", "design-assets", "org", "project", "a.svg")).Should().BeTrue();
        await using var stream = await storage.DownloadAsync("design-assets", "org/project/a.svg");
        new StreamReader(stream!).ReadToEnd().Should().Be("<svg/>");
    }

    [Fact]
    public async Task DownloadUrl_IsSignedForGetOnThatBlobOnly()
    {
        var url = await Storage().GenerateDownloadUrlAsync("design-assets", "org/p/a.svg", TimeSpan.FromMinutes(5));
        var (exp, sig) = QueryOf(url);

        url.Should().StartWith("http://127.0.0.1:6750/blobs/design-assets/org/p/a.svg?");
        _signer.Verify("GET", "design-assets", "org/p/a.svg", exp, sig).Should().BeTrue();
        _signer.Verify("PUT", "design-assets", "org/p/a.svg", exp, sig).Should().BeFalse();
        _signer.Verify("GET", "design-assets", "org/p/b.svg", exp, sig).Should().BeFalse();
        _signer.Verify("GET", "other", "org/p/a.svg", exp, sig).Should().BeFalse();
    }

    [Fact]
    public async Task UploadUrl_IsSignedForPut()
    {
        var url = await Storage().GenerateUploadUrlAsync("attachments", "org/t/file.png", "image/png", TimeSpan.FromMinutes(5));
        var (exp, sig) = QueryOf(url);

        _signer.Verify("PUT", "attachments", "org/t/file.png", exp, sig).Should().BeTrue();
        _signer.Verify("GET", "attachments", "org/t/file.png", exp, sig).Should().BeFalse();
    }

    [Fact]
    public void Signature_Expires_AndCannotBeExtendedByEditingExp()
    {
        var query = _signer.Sign("GET", "c", "b", TimeSpan.FromSeconds(-1));
        var parsed = System.Web.HttpUtility.ParseQueryString(query);

        _signer.Verify("GET", "c", "b", parsed["exp"], parsed["sig"]).Should().BeFalse();
        var later = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString();
        _signer.Verify("GET", "c", "b", later, parsed["sig"]).Should().BeFalse();
    }

    [Theory]
    [InlineData("..", "x")]
    [InlineData("c", "../../secrets.txt")]
    [InlineData("c", "a/../../../x")]
    [InlineData("c/../..", "x")]
    public void GetPath_RefusesToEscapeTheBlobRoot(string container, string blob)
    {
        var act = () => Storage().GetPath(container, blob);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Signer_DerivesTheSameKeyFromTheSameEncryptionKey()
    {
        var encryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Key"] = encryptionKey })
            .Build();

        var query = System.Web.HttpUtility.ParseQueryString(
            new BlobUrlSigner(config).Sign("GET", "c", "b", TimeSpan.FromMinutes(1)));

        new BlobUrlSigner(config).Verify("GET", "c", "b", query["exp"], query["sig"])
            .Should().BeTrue("URLs handed out before a restart keep working");
        Encoding.UTF8.GetByteCount(query["sig"]!).Should().Be(64);
    }
}
