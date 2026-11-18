namespace Orkka.Local.Infrastructure;

using Microsoft.AspNetCore.Routing;

public sealed class UlidRouteConstraint : IRouteConstraint
{
    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        if (values.TryGetValue(routeKey, out var value) && value is string str)
            return Ulid.TryParse(str, out _);

        return false;
    }
}
