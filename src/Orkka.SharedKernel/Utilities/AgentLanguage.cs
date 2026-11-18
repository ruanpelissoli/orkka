namespace Orkka.SharedKernel.Utilities;

/// <summary>
/// Maps a language code to the language instruction injected into agent prompts. The code
/// comes from the project's language setting (ProjectLanguage.Resolve), falling back to the
/// requester's UI locale for projects that predate the setting.
/// Injected dynamically at prompt-build time — never baked into seeded agent
/// prompts (project_agents.SystemPrompt is frozen at seed time).
/// </summary>
public static class AgentLanguage
{
    public const string Default = "en";

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English",
        ["es"] = "Spanish",
        ["pt"] = "Portuguese (Brazilian)",
        ["fr"] = "French",
        ["de"] = "German",
    };

    /// <summary>
    /// Normalizes locale values like "pt-BR", "PT", or an Accept-Language list
    /// ("pt-BR,pt;q=0.9") to a supported two-letter code, or null if unsupported.
    /// </summary>
    public static string? Normalize(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        var code = locale.Split(',')[0].Split(';')[0].Trim();
        if (code.Length > 2) code = code[..2];
        return Names.ContainsKey(code) ? code.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Display name of a supported non-default locale ("Portuguese (Brazilian)"), or null
    /// when the locale is null/en/unsupported — i.e. when nothing needs to be said.
    /// </summary>
    public static string? DisplayName(string? locale)
    {
        var code = Normalize(locale);
        return code is null or Default ? null : Names[code];
    }

    /// <summary>
    /// Language instruction to append to a system prompt. Returns "" for
    /// null/en/unknown locales — English needs no instruction.
    /// </summary>
    public static string Instruction(string? locale)
    {
        if (DisplayName(locale) is not { } name) return "";
        return $"""


            ## Response Language
            This project's working language is {name}. Write ALL user-facing text in {name}:
            chat responses, progress updates and the reasoning you narrate while working,
            clarifying questions, plan summaries and approach descriptions, review summaries
            and review comments, task titles and task descriptions, and every document you
            generate (briefs, PRDs, specs, project documentation, reports).
            This is decided by the project's language setting, NOT by the language of the
            message you are answering: keep writing in {name} even when the user writes to you
            in English or any other language, and even when earlier replies in this
            conversation are in another language.
            Always keep the following in English: code, identifiers, file paths, branch names,
            commit messages, JSON keys and enum values in structured responses (e.g. "decision",
            "priority", "type" values), and shell commands.
            """;
    }

    /// <summary>
    /// One-line reminder for the END of the model's input (after the user's message).
    /// <see cref="Instruction"/> lands mid-system-prompt, ahead of the rolling summary and
    /// the conversation history, so it loses salience exactly as the conversation grows —
    /// which is why replies drift back to English (or to the language the user happened to
    /// type in) only on some turns. Same fix as the questions-protocol reminder in
    /// ChatTurnRunner: restate it last. Returns "" for null/en/unknown locales.
    /// </summary>
    public static string Reminder(string? locale)
    {
        if (DisplayName(locale) is not { } name) return "";
        return $"\n\n(Language reminder — not part of the user's message: write your reply in " +
               $"{name}, whatever language this message is written in. Code, identifiers, file " +
               $"paths, commands and commit messages stay in English.)";
    }

    /// <summary>
    /// Instruction for scaffold code generation: only the generated app's
    /// user-facing copy is localized; everything else stays English.
    /// </summary>
    public static string ScaffoldCopyInstruction(string? locale)
    {
        if (DisplayName(locale) is not { } name) return "";
        return $"""


            ## UI Copy Language
            All user-facing copy in the generated UI (headings, labels, buttons, placeholder
            text, empty states, seed data display strings) must be written in {name}.
            All code, identifiers, comments, file names, and the Prisma schema remain in English.
            """;
    }
}
