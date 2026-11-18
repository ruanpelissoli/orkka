namespace Orkka.Tests.Services;

using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

public sealed class AesEncryptionServiceTests
{
    private static AesEncryptionService CreateService(string? keyBase64 = null)
    {
        keyBase64 ??= Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:Key"] = keyBase64
            })
            .Build();

        return new AesEncryptionService(config);
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_ReturnsOriginalValue()
    {
        var service = CreateService();
        var original = "sk-ant-api03-test-key-1234567890";

        var encrypted = service.Encrypt(original);
        var decrypted = service.Decrypt(encrypted);

        decrypted.Should().Be(original);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var service = CreateService();
        var original = "sk-ant-api03-test-key-1234567890";

        var encrypted1 = service.Encrypt(original);
        var encrypted2 = service.Encrypt(original);

        encrypted1.Should().NotBe(encrypted2, "each encryption uses a random nonce");
    }

    [Fact]
    public void Decrypt_WithDifferentKey_Throws()
    {
        var service1 = CreateService();
        var service2 = CreateService(); // different key

        var encrypted = service1.Encrypt("my-secret-key");

        var act = () => service2.Decrypt(encrypted);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Encrypt_WithEmptyString_Throws()
    {
        var service = CreateService();

        var act = () => service.Encrypt("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decrypt_WithEmptyString_Throws()
    {
        var service = CreateService();

        var act = () => service.Decrypt("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decrypt_WithTamperedData_Throws()
    {
        var service = CreateService();
        var encrypted = service.Encrypt("my-secret-key");

        var bytes = Convert.FromBase64String(encrypted);
        bytes[^1] ^= 0xFF; // flip last byte
        var tampered = Convert.ToBase64String(bytes);

        var act = () => service.Decrypt(tampered);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Constructor_WithMissingKey_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var act = () => new AesEncryptionService(config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Encryption key not configured*");
    }

    [Fact]
    public void Constructor_WithWrongKeyLength_Throws()
    {
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var act = () => CreateService(shortKey);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*256 bits*");
    }

    [Fact]
    public void Encrypt_Decrypt_HandlesUnicodeContent()
    {
        var service = CreateService();
        var original = "sk-🔑-unicode-key-日本語";

        var decrypted = service.Decrypt(service.Encrypt(original));
        decrypted.Should().Be(original);
    }

    [Fact]
    public void Encrypt_Decrypt_HandlesLongKeys()
    {
        var service = CreateService();
        var original = new string('x', 1000);

        var decrypted = service.Decrypt(service.Encrypt(original));
        decrypted.Should().Be(original);
    }
}
