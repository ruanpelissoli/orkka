namespace Orkka.Modules.Projects.Contracts;

using Orkka.SharedKernel.Utilities;

/// <summary>
/// Which language the agents of a project work in. One rule, applied wherever a prompt is
/// built: the project's own <see cref="ProjectSettingsDto.Language"/> wins; a project that
/// has none (created before the setting existed) keeps following the locale frozen on the
/// task/conversation by the human who drove it, exactly as before the setting existed.
/// </summary>
/// <remarks>
/// Resolved at prompt-build time rather than copied onto tasks at creation, so changing the
/// project's language takes effect on the next agent turn of every existing task and chat —
/// nobody expects a language switch to apply only to tasks created afterwards.
/// </remarks>
public static class ProjectLanguage
{
    /// <summary>
    /// The language agents should write in: the project setting when set and supported,
    /// else <paramref name="requesterLocale"/> (may be null — callers pass that straight to
    /// <see cref="AgentLanguage.Instruction"/>, where null/"en" mean "no instruction").
    /// </summary>
    public static string? Resolve(ProjectSettingsDto? settings, string? requesterLocale)
        => AgentLanguage.Normalize(settings?.Language) ?? requesterLocale;

    /// <summary>Same rule over a <see cref="ProjectSummary"/>.</summary>
    public static string? Resolve(ProjectSummary? project, string? requesterLocale)
        => Resolve(project?.Settings, requesterLocale);
}
