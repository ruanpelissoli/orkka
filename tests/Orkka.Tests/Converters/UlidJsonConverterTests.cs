namespace Orkka.Tests.Converters;

using System.Text.Json;
using Orkka.SharedKernel.Utilities;
using FluentAssertions;

public sealed class UlidJsonConverterTests
{
    private readonly JsonSerializerOptions _options = new()
    {
        Converters = { new UlidJsonConverter() }
    };

    [Fact]
    public void Serialize_ProducesBase32String()
    {
        var ulid = Ulid.NewUlid();

        var json = JsonSerializer.Serialize(ulid, _options);

        json.Should().Be($"\"{ulid}\"");
        // ULID string is 26 chars, plus 2 quotes = 28
        json.Should().HaveLength(28);
    }

    [Fact]
    public void Deserialize_FromValidUlidString()
    {
        var ulid = Ulid.NewUlid();
        var json = $"\"{ulid}\"";

        var result = JsonSerializer.Deserialize<Ulid>(json, _options);

        result.Should().Be(ulid);
    }

    [Fact]
    public void RoundTrip_PreservesValue()
    {
        var ulid = Ulid.NewUlid();

        var json = JsonSerializer.Serialize(ulid, _options);
        var result = JsonSerializer.Deserialize<Ulid>(json, _options);

        result.Should().Be(ulid);
    }

    [Fact]
    public void Deserialize_InvalidString_Throws()
    {
        var json = "\"not-a-valid-ulid\"";

        var act = () => JsonSerializer.Deserialize<Ulid>(json, _options);

        act.Should().Throw<Exception>();
    }
}
