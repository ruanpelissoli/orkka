namespace Orkka.Modules.Organizations.Features.Members;


public sealed record MeResponse(
    string OrgId,
    string OrgName,
    string MemberId,
    string Role,
    string[] Permissions,
    MemberPreferences Preferences);

/// <summary>Per-member UI preferences, stored as JSON on OrgMember. Every field must
/// default to the "simple mode" value so a null/absent JSON needs no backfill.</summary>
/// <param name="ShowTechnicalDetails">Advanced mode: repos, deployments, model pickers.</param>
/// <param name="OnboardingTourSeen">Set once the product tour is finished or skipped. Members
/// who predate the tour parse as false and so get it on their next Ready project — it is a
/// one-time introduction to the app, not a versioned changelog.</param>
/// <param name="Locale">The interface language the member picked ("pt", "es", ...). Null means
/// they never picked one, and the request's Accept-Language decides. Unlike the flags above
/// this leaves the UI: the auth middleware promotes it to CurrentUser.Locale, which is what
/// tells every agent — chat, tasks, schedules — which language to answer in.</param>
public sealed record MemberPreferences(
    bool ShowTechnicalDetails = false,
    bool OnboardingTourSeen = false,
    string? Locale = null)
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static MemberPreferences Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new MemberPreferences();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<MemberPreferences>(json, JsonOptions)
                ?? new MemberPreferences();
        }
        catch (System.Text.Json.JsonException)
        {
            return new MemberPreferences();
        }
    }

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>Partial update: every field is optional and an omitted (null) one leaves the stored
/// value alone. Callers touch one preference at a time, so a required field would make the tour
/// flag silently reset the technical-details toggle.</summary>
public sealed record UpdateMyPreferencesRequest(
    bool? ShowTechnicalDetails = null,
    bool? OnboardingTourSeen = null,
    string? Locale = null);
