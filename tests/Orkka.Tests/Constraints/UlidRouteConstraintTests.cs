namespace Orkka.Tests.Constraints;

using Orkka.Local.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;

public sealed class UlidRouteConstraintTests
{
    private readonly UlidRouteConstraint _constraint = new();

    [Fact]
    public void ValidUlid_ReturnsTrue()
    {
        var ulid = Ulid.NewUlid().ToString();
        var values = new RouteValueDictionary { ["id"] = ulid };

        var result = _constraint.Match(null, null!, "id", values, RouteDirection.IncomingRequest);

        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("not-a-ulid")]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")] // GUID format
    public void InvalidStrings_ReturnFalse(string value)
    {
        var values = new RouteValueDictionary { ["id"] = value };

        var result = _constraint.Match(null, null!, "id", values, RouteDirection.IncomingRequest);

        result.Should().BeFalse();
    }

    [Fact]
    public void MissingRouteKey_ReturnsFalse()
    {
        var values = new RouteValueDictionary();

        var result = _constraint.Match(null, null!, "id", values, RouteDirection.IncomingRequest);

        result.Should().BeFalse();
    }

    [Fact]
    public void NonStringValue_ReturnsFalse()
    {
        var values = new RouteValueDictionary { ["id"] = 12345 };

        var result = _constraint.Match(null, null!, "id", values, RouteDirection.IncomingRequest);

        result.Should().BeFalse();
    }
}
