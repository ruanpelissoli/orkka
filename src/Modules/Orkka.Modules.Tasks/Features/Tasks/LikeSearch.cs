namespace Orkka.Modules.Tasks.Features.Tasks;

internal static class LikeSearch
{
    /// <summary>
    /// Builds a %contains% pattern with LIKE metacharacters escaped so user input
    /// matches literally. Must be used with the 3-arg EF.Functions.Like overload and
    /// <c>@"\"</c> as the escape character (SQLite LIKE has no default escape). SQLite LIKE
    /// ignores case for ASCII letters only: "é" does not match "É".
    /// </summary>
    public static string ContainsPattern(string query)
    {
        var escaped = query.Trim()
            .Replace(@"\", @"\\")
            .Replace("%", @"\%")
            .Replace("_", @"\_");
        return $"%{escaped}%";
    }
}
