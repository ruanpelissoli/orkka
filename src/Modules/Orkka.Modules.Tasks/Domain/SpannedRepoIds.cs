namespace Orkka.Modules.Tasks.Domain;

using System.Text.Json;

/// <summary>
/// Serialization for <see cref="TaskGroup.SpansRepoIdsJson"/>.
///
/// A JSON array of ULID strings rather than a join table: the list is written once at creation,
/// read once by the split, and never queried across groups. A table would buy indexing nothing
/// asks for and a second round trip on every read.
///
/// Parsing is total — malformed or absent JSON yields an empty list, never an exception. The
/// split treats "no spanned repos" as "not actually a spanning task" and lets the task run
/// alone, which is the right outcome for a corrupt row too.
/// </summary>
public static class SpannedRepoIds
{
    public static string? Serialize(IEnumerable<Ulid>? repoIds)
    {
        var ids = repoIds?.Distinct().Select(id => id.ToString()).ToList();
        return ids is { Count: > 0 } ? JsonSerializer.Serialize(ids) : null;
    }

    public static IReadOnlyList<Ulid> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var raw = JsonSerializer.Deserialize<List<string>>(json);
            if (raw is null)
                return [];

            List<Ulid> ids = [];
            foreach (var value in raw)
            {
                if (Ulid.TryParse(value, out var id))
                    ids.Add(id);
            }

            return ids;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
