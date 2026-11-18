namespace Orkka.Tests.Converters;

using FluentAssertions;

public sealed class UlidToGuidConverterTests
{
    private readonly UlidToGuidConverter _converter = new();

    [Fact]
    public void RoundTrip_PreservesValue()
    {
        var ulid = Ulid.NewUlid();

        var guid = (Guid)_converter.ConvertToProvider(ulid)!;
        var result = (Ulid)_converter.ConvertFromProvider(guid)!;

        result.Should().Be(ulid);
    }

    [Fact]
    public void NewUlid_ConvertsToNonEmptyGuid()
    {
        var ulid = Ulid.NewUlid();

        var guid = (Guid)_converter.ConvertToProvider(ulid)!;

        guid.Should().NotBeEmpty();
    }

    [Fact]
    public void Empty_ConvertsToEmptyGuid()
    {
        var guid = (Guid)_converter.ConvertToProvider(Ulid.Empty)!;

        guid.Should().Be(Guid.Empty);
    }

    [Fact]
    public void MultipleUlids_WithDifferentTimestamps_MaintainOrdering()
    {
        var ulid1 = Ulid.NewUlid(DateTimeOffset.UtcNow.AddSeconds(-1));
        var ulid2 = Ulid.NewUlid(DateTimeOffset.UtcNow);

        ulid2.CompareTo(ulid1).Should().BeGreaterThanOrEqualTo(0);
    }
}

public sealed class NullableUlidToGuidConverterTests
{
    private readonly NullableUlidToGuidConverter _converter = new();

    [Fact]
    public void NullUlid_ConvertsToNullGuid()
    {
        var result = _converter.ConvertToProvider(null);

        result.Should().BeNull();
    }

    [Fact]
    public void NullGuid_ConvertsToNullUlid()
    {
        var result = _converter.ConvertFromProvider(null);

        result.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_WithValue_PreservesValue()
    {
        Ulid? ulid = Ulid.NewUlid();

        var guid = (Guid?)_converter.ConvertToProvider(ulid);
        var result = (Ulid?)_converter.ConvertFromProvider(guid);

        result.Should().Be(ulid);
    }
}
