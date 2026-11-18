namespace Orkka.Tests.Auth;

using Orkka.Local.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;

public sealed class CurrentUserTests
{
    private static CurrentUser CreateCurrentUser(HttpContext? httpContext = null)
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(httpContext);
        return new CurrentUser(accessor.Object);
    }

    [Fact]
    public void UserId_ReturnsValue_WhenSetInHttpContextItems()
    {
        var context = new DefaultHttpContext();
        context.Items[CurrentUserKeys.UserId] = "user_123";

        var sut = CreateCurrentUser(context);

        sut.UserId.Should().Be("user_123");
    }

    [Fact]
    public void OrgId_ReturnsValue_WhenSetInHttpContextItems()
    {
        var context = new DefaultHttpContext();
        var orgId = Ulid.NewUlid();
        context.Items[CurrentUserKeys.OrgId] = orgId;

        var sut = CreateCurrentUser(context);

        sut.OrgId.Should().Be(orgId);
    }

    [Fact]
    public void MemberId_ReturnsValue_WhenSetInHttpContextItems()
    {
        var context = new DefaultHttpContext();
        var memberId = Ulid.NewUlid();
        context.Items[CurrentUserKeys.MemberId] = memberId;

        var sut = CreateCurrentUser(context);

        sut.MemberId.Should().Be(memberId);
    }

    [Fact]
    public void UserId_ThrowsInvalidOperationException_WhenNotSet()
    {
        var context = new DefaultHttpContext();
        var sut = CreateCurrentUser(context);

        var act = () => sut.UserId;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CurrentUser*not resolved*");
    }

    [Fact]
    public void OrgId_ThrowsInvalidOperationException_WhenNotSet()
    {
        var context = new DefaultHttpContext();
        var sut = CreateCurrentUser(context);

        var act = () => sut.OrgId;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void UserId_ThrowsInvalidOperationException_WhenNoHttpContext()
    {
        var sut = CreateCurrentUser(httpContext: null);

        var act = () => sut.UserId;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No HttpContext*");
    }
}
