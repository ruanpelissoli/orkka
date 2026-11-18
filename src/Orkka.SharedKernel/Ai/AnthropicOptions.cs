namespace Orkka.SharedKernel.Ai;

public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>Model used for quality-critical utility API calls (brief synthesis, task
    /// decomposition, scaffolding) when no <see cref="DefaultModel"/> override is configured.</summary>
    public const string FallbackUtilityModel = "claude-sonnet-5";

    /// <summary>Model used for cheap background utility calls (conversation titles, rolling
    /// summaries, dependency analysis) when no <see cref="DefaultModel"/>
    /// override is configured.</summary>
    public const string FallbackFastUtilityModel = "claude-haiku-4-5";

    /// <summary>Model used for the project-briefing chat (deep requirements elicitation)
    /// when no <see cref="DefaultModel"/> override is configured.</summary>
    public const string FallbackBriefModel = "claude-sonnet-5";

    /// <summary>Model used to plan work — goal → milestone + tasks, cross-repo contract splits —
    /// when no <see cref="DefaultModel"/> override is configured. Kept as its own knob.</summary>
    public const string FallbackPlannerModel = "claude-sonnet-5";

    /// <summary>Optional HARD override of every model choice, including per-agent models.
    /// Leave empty to use each agent's configured model (normal Anthropic operation);
    /// set it to pin one model everywhere.</summary>
    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>Model for quality-critical utility API calls: the override when set, else the fallback.</summary>
    public string UtilityModel =>
        string.IsNullOrWhiteSpace(DefaultModel) ? FallbackUtilityModel : DefaultModel;

    /// <summary>Model for cheap background utility calls: the override when set, else the fallback.</summary>
    public string FastUtilityModel =>
        string.IsNullOrWhiteSpace(DefaultModel) ? FallbackFastUtilityModel : DefaultModel;

    /// <summary>Model for the project-briefing chat: the override when set, else the fallback.</summary>
    public string BriefModel =>
        string.IsNullOrWhiteSpace(DefaultModel) ? FallbackBriefModel : DefaultModel;

    /// <summary>Model for planning work (task decomposition, cross-repo splits): the override
    /// when set, else the fallback.</summary>
    public string PlannerModel =>
        string.IsNullOrWhiteSpace(DefaultModel) ? FallbackPlannerModel : DefaultModel;
}
