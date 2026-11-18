namespace Orkka.Modules.Agents.Features.AgentChat;

/// <summary>
/// Cadence of the rolling conversation-summary fold (config section "AgentChat:Summary").
/// Each fold is an extra utility model call — on Orkka Local a full CLI spawn on the
/// user's subscription — so the cadence is tunable per edition.
/// </summary>
public sealed class ChatSummaryOptions
{
    /// <summary>Messages kept live (injected raw into the prompt) and never folded.</summary>
    public int KeepRecentMessages { get; set; } = 8;

    /// <summary>Minimum unfolded messages beyond the recent window before a fold runs.</summary>
    public int BatchMinMessages { get; set; } = 4;

    /// <summary>
    /// Skip folding while the conversation holds a resumable CLI session. The summary is
    /// only consumed when a session can't be resumed (fresh rebuild), so folding on every
    /// batch pays for summaries that usually go unused. When the session invalidates, the
    /// next turn folds the accumulated backlog in one call. Off by default (hosted keeps
    /// the summary warm); Orkka Local turns it on.
    /// </summary>
    public bool SkipWhileResumable { get; set; }
}
