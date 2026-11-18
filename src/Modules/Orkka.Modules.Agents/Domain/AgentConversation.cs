namespace Orkka.Modules.Agents.Domain;

using Orkka.SharedKernel.Domain;

public sealed class AgentConversation : TenantEntity
{
    // Cross-module reference (Projects module) — plain id, no navigation.
    public Ulid ProjectId { get; set; }
    public Ulid AgentId { get; set; }

    /// <summary>The task this conversation discusses (Tasks module — plain id, no navigation).
    /// Null for the Team page's agent chat. A task conversation is the task's "Discuss" tab:
    /// one per (task, agent), never listed on the Team page, run read-only on the task branch.</summary>
    public Ulid? TaskId { get; set; }

    public string? Title { get; set; }

    /// <summary>Rolling summary of messages older than the recent window — injected into
    /// the agent's system prompt instead of the raw messages so long conversations stay
    /// within the prompt budget (Windows command-line limit) without losing context.</summary>
    public string? ContextSummary { get; set; }

    /// <summary>How many of the conversation's oldest messages (by CreatedAt) are already
    /// folded into <see cref="ContextSummary"/>. Messages are append-only, so a count
    /// works as a watermark.</summary>
    public int SummarizedMessageCount { get; set; }

    /// <summary>Claude CLI session id of the conversation's last successful turn. Passed as
    /// Resume on the next turn so the CLI reuses its warm session (no cold boot, no history
    /// re-injection) instead of starting over. Session files live on the machine that ran
    /// the turn (~/.claude/projects) — when resuming fails there (different runner/replica,
    /// cleanup), the turn is retried with a fresh session and this id is replaced.</summary>
    public string? ClaudeSessionId { get; set; }

    /// <summary>AiProvider enum name the stored session id belongs to (codex thread ids and
    /// claude session ids are not interchangeable). Null on pre-existing rows means
    /// Anthropic — the only provider that stored sessions before this column existed.</summary>
    public string? SessionProvider { get; set; }

    /// <summary>When the agent reply currently being generated started, null when idle.
    /// Chat runs are detached from the SSE connection, so a reopened panel uses this to
    /// show the thinking indicator. Cleared when the run finishes; a marker that outlives
    /// a crashed process is neutralized by the staleness window in the response mapper.</summary>
    public DateTimeOffset? ActiveRunStartedAt { get; set; }

    // Navigation (intra-module)
    public ProjectAgent Agent { get; set; } = null!;
    public ICollection<AgentMessage> Messages { get; set; } = [];
}
