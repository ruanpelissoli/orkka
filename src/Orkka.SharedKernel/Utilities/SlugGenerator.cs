namespace Orkka.SharedKernel.Utilities;

public static class SlugGenerator
{
    public static string GenerateRepoName(string projectName)
    {
        return projectName
            .ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-")
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .Aggregate("", (s, c) => s + c)
            .Trim('-');
    }

    public static string GenerateSlug(string projectName, Ulid projectId)
    {
        var nameSlug = GenerateRepoName(projectName);
        var shortId = projectId.ToString()[..8].ToLowerInvariant();
        return $"{nameSlug}_{shortId}";
    }

    /// <summary>
    /// Managed repos share one GitHub org across all tenants, so the name must be unique by
    /// construction — the full project-id suffix removes both cross-tenant collisions and the
    /// existence oracle a pre-flight name check would create. (A truncated ULID would not do:
    /// its leading characters are timestamp-derived and collide within the same millisecond.)
    /// </summary>
    public static string GenerateManagedRepoName(string projectName, Ulid projectId)
    {
        var idSuffix = projectId.ToString().ToLowerInvariant();
        var nameSlug = GenerateRepoName(projectName);
        if (nameSlug.Length == 0)
            nameSlug = "app";

        // GitHub repo names cap at 100 chars; the id suffix must always survive truncation.
        var maxNameLength = 100 - idSuffix.Length - 1;
        if (nameSlug.Length > maxNameLength)
            nameSlug = nameSlug[..maxNameLength].Trim('-');

        return $"{nameSlug}-{idSuffix}";
    }

    /// <summary>
    /// Git branch name for a milestone's integration branch — what its tasks fork from and
    /// merge into, and what merges to the default branch once when the milestone completes.
    ///
    /// The suffix is the ULID's TAIL for the same reason as <see cref="GenerateAppHostLabel"/>:
    /// the leading characters are timestamp-derived, and a decomposition that seeds several
    /// milestones in one loop creates them inside the same millisecond. Two milestones sharing a
    /// branch would silently merge one's work into the other's ship.
    /// </summary>
    public static string GenerateMilestoneBranchName(string title, Ulid milestoneId)
    {
        var idSuffix = milestoneId.ToString()[^8..].ToLowerInvariant();

        // ASCII only, deliberately — unlike GenerateRepoName, which keeps any Unicode letter
        // because char.IsLetterOrDigit says yes to 'ç'. git would accept "ação-são-paulo" as a
        // ref, but this name then travels through GitHub API path segments, the runner wire and
        // Windows worktree paths, and every one of those is a place an encoding difference turns
        // into a branch that cannot be found. Runs of dashes are collapsed for the same reason
        // GenerateAppHostLabel collapses them: humans read this in PR lists all day.
        var nameSlug = System.Text.RegularExpressions.Regex.Replace(
            new string([.. title.ToLowerInvariant()
                .Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-')]),
            "-{2,}", "-").Trim('-');

        if (nameSlug.Length == 0)
            nameSlug = "milestone";

        // Keep the whole ref comfortably short — long branch names are unreadable in PR lists
        // and git UIs, and the suffix is what carries uniqueness anyway.
        const int maxNameLength = 40;
        if (nameSlug.Length > maxNameLength)
            nameSlug = nameSlug[..maxNameLength].Trim('-');

        return $"orkka/milestone/{nameSlug}-{idSuffix}";
    }

    /// <summary>
    /// DNS label for a managed app's platform hostname ({label}.orkka.app). Separate from
    /// GenerateManagedRepoName because a DNS label caps at 63 characters, where the full ULID
    /// leaves too little room for a readable name. The suffix is the ULID's TAIL, not its head:
    /// the leading characters are timestamp-derived and collide for projects created in the same
    /// millisecond, and a hostname collision across tenants would point one org's domain at
    /// another org's app.
    /// </summary>
    public static string GenerateAppHostLabel(string projectName, Ulid projectId)
    {
        const int maxLabelLength = 63;
        var idSuffix = projectId.ToString()[^8..].ToLowerInvariant();

        // Unlike GenerateRepoName, disallowed characters become '-' rather than vanishing, so
        // "Plant Me" and "PlantMe" stay distinguishable; runs are then collapsed because "--" in
        // the third and fourth positions of a label is reserved for IDN A-labels.
        var nameSlug = new string([.. projectName
            .ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        while (nameSlug.Contains("--"))
            nameSlug = nameSlug.Replace("--", "-");
        nameSlug = nameSlug.Trim('-');

        // The id suffix must always survive truncation — it is what makes the label unique.
        var maxNameLength = maxLabelLength - idSuffix.Length - 1;
        if (nameSlug.Length > maxNameLength)
            nameSlug = nameSlug[..maxNameLength].Trim('-');
        if (nameSlug.Length == 0)
            nameSlug = "app";

        return $"{nameSlug}-{idSuffix}";
    }

    // Route literals under /api/projects/ that a slug must never collide with
    public static readonly string[] ReservedProjectSlugs = ["brief"];

    public static string GenerateProjectSlug(string name)
    {
        var slug = name
            .ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .Aggregate("", (s, c) => s + c);

        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        slug = slug.Trim('-');

        if (slug.Length == 0)
            slug = "project";
        if (slug.Length > 100)
            slug = slug[..100].Trim('-');
        if (ReservedProjectSlugs.Contains(slug))
            slug += "-1";

        return slug;
    }

    public static string GenerateProjectKey(string name)
    {
        var words = name
            .Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Any(char.IsAsciiLetter))
            .ToArray();

        var initials = new string(words
            .Select(w => w.First(char.IsAsciiLetter))
            .Take(5)
            .ToArray())
            .ToUpperInvariant();

        if (initials.Length >= 2)
            return initials;

        var letters = new string(name.Where(char.IsAsciiLetter).Take(3).ToArray()).ToUpperInvariant();
        return letters.Length >= 2 ? letters : "PRJ";
    }
}
