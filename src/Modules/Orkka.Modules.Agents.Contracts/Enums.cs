namespace Orkka.Modules.Agents.Contracts;

public enum AgentRole
{
    PM,
    Designer,
    FrontendDev,
    BackendDev,
    FullStackDev,
    QA,
    CodeReviewer,
    DevOps,
    SolutionArchitect,
    GameDev,
    GameDesigner,
    DBA,
    Legal,
    MobileDev
}

public static class AgentRoleExtensions
{
    /// <summary>
    /// Roles that write code and are eligible for Planning/Developing lane assignment.
    /// Must stay in sync with the ordered priority list in AgentAssigner.DevRoles
    /// (pinned by AgentAssignerTests).
    /// </summary>
    public static bool IsDevRole(this AgentRole role) =>
        role is AgentRole.FrontendDev or AgentRole.BackendDev
            or AgentRole.FullStackDev or AgentRole.GameDev or AgentRole.MobileDev;

    /// <summary>
    /// Roles whose output is code the customer keeps — the audience for prompt rules about how
    /// code is written (see SharedPromptBlocks.CodeCommentPolicy). Wider than IsDevRole because
    /// DevOps authors Dockerfiles, workflows and Terraform; narrower than "all agents" because
    /// QA, CodeReviewer, PM, Designer, SolutionArchitect, DBA and Legal either judge code or
    /// produce prose, and a writing rule aimed at them would only cost tokens.
    /// </summary>
    public static bool IsCodeWritingRole(this AgentRole role) =>
        role.IsDevRole() || role is AgentRole.DevOps;

    /// <summary>
    /// Roles that render screens the customer's users look at — the audience for the visual
    /// bar (see SharedPromptBlocks.VisualBar). Narrower than IsDevRole: BackendDev and DevOps
    /// ship no UI, and GameDev/MobileDev build against conventions a web-flavoured bar would
    /// misdirect (they carry the `ui-craft` and `brand-consistency` skills instead, which are
    /// read on demand and can be adapted to their platform).
    /// <para>
    /// Must stay in sync with the personas that bake the block in — DefaultPromptsTests pins
    /// the agreement, because a mismatch means an agent gets the bar twice or never.
    /// </para>
    /// </summary>
    public static bool IsUiBuildingRole(this AgentRole role) =>
        role is AgentRole.FrontendDev or AgentRole.FullStackDev;
}

public enum MemoryType
{
    /// <summary>Something that worked / was learned about the codebase.</summary>
    Learning,
    /// <summary>Something that went wrong and how to avoid it.</summary>
    Mistake,
    /// <summary>A recurring shape in the code or process worth naming.</summary>
    Pattern,
    /// <summary>How the humans like things done.</summary>
    Preference,
    /// <summary>A choice the team made and its reason (architecture, library, approach).</summary>
    Decision,
    /// <summary>A rule of this project: naming, layout, testing, review expectations.</summary>
    Convention,
}

/// <summary>Who wrote a memory.</summary>
public enum MemorySource
{
    /// <summary>An agent, through the remember tool or the post-run outcome extractor.</summary>
    Agent,
    /// <summary>A human, through the Memory page.</summary>
    User,
    /// <summary>The platform (seeding, imports).</summary>
    System,
}

/// <summary>
/// Categories of security-relevant agent activity captured in the immutable audit log.
/// </summary>
public enum AgentAuditEventType
{
    /// <summary>An agent run started for a phase.</summary>
    RunStarted,

    /// <summary>An agent run finished normally.</summary>
    RunFinished,

    /// <summary>The agent invoked a tool (Bash, Write, Edit, etc.).</summary>
    ToolUse,

    /// <summary>The Bash guard blocked a restricted command.</summary>
    CommandBlocked,

    /// <summary>A per-run cap (tool-call or wall-clock) stopped the run.</summary>
    LimitReached,

    /// <summary>A pull request was created on the customer's repository.</summary>
    PullRequestCreated,

    /// <summary>A pull request was merged into the customer's repository.</summary>
    PullRequestMerged,

    /// <summary>The run produced an error.</summary>
    Error,

    // Hybrid runner lifecycle (append-only — never reorder):

    /// <summary>A user's runner daemon connected to the hosted hub.</summary>
    RunnerConnected,

    /// <summary>A user's runner daemon disconnected from the hosted hub.</summary>
    RunnerDisconnected,

    /// <summary>A runner access token was created.</summary>
    RunnerTokenCreated,

    /// <summary>A runner access token was revoked.</summary>
    RunnerTokenRevoked,

    // ── added with the audit hardening (Aug 2026): people and configuration, not just runs ──

    /// <summary>A workspace was created (with its owner's membership).</summary>
    AccountProvisioned,
    /// <summary>Project settings were changed (metadata lists the keys).</summary>
    ProjectSettingsChanged,
    /// <summary>An agent's configuration (name, model, instructions, scope, skills) was changed.</summary>
    AgentConfigChanged,
    SkillChanged,
    ScheduleChanged,
    /// <summary>An org API key was minted / revoked.</summary>
    ApiKeyCreated,
    ApiKeyRevoked,
    /// <summary>A tool call over the MCP endpoint (Tool = tool name, ActorKind = ApiKey/User).</summary>
    McpToolCall,
    /// <summary>A project memory was created / edited / pinned / archived / deleted by a human.</summary>
    MemoryChanged,
    /// <summary>A project's ticket source (tracker connection, scope, epics, status map) was set,
    /// changed or removed.</summary>
    TrackerConfigChanged,
    /// <summary>A tracker connection was added or removed from the settings page (kind and site
    /// only — never the token, which goes straight to the developer's machine).</summary>
    TrackerConnectionChanged,
}

public enum DesignAssetStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>Stored by name. Agent-made assets are always Svg (Orkka generates no raster images
/// or GIFs). Image and Gif are what a user's own upload is filed as, and also what assets
/// generated before server-side image generation was removed carry.</summary>
public enum DesignAssetKind
{
    Svg,
    Image,
    Gif
}

public enum DiagramType
{
    SystemArchitecture,
    DataFlow,
    NetworkTopology,
    SequenceDiagram,
    ERDiagram,
    DeploymentDiagram,
    ComponentDiagram,
    Other
}

/// <summary>Which AI vendor serves a given model (agents carry a per-agent model choice).
/// Stored/transmitted by name in places — append new values, never reorder.</summary>
public enum AiProvider
{
    Anthropic,
    OpenAI,
    Google,
    GitHubCopilot,
    Ollama,
    // Retired image-only vendor (PixelLab pixel-art generation, removed with server-side
    // image generation). Kept so legacy AiModelKind.Image catalog rows still load; no agent
    // invoker dispatches on it and nothing offers it.
    PixelLab,
    // xAI, driven through the `grok` CLI (Grok Build). A conventional vendor: it ships both
    // the models and the agentic harness, like Anthropic/OpenAI/Google.
    XAI,
    // NOT a vendor — an agentic CLI harness (sst/opencode) that brings no models of its own
    // and drives whatever provider the user authenticated via `opencode auth login`. Modelled
    // as a provider because that is the axis Orkka routes invokers on; its catalog ids carry
    // the real vendor inside them ("opencode/anthropic/claude-sonnet-4-6") and the selectable
    // set is whatever the runner reports, never a static list. Contrast Ollama, which is the
    // mirror image: models but no harness (so it borrows the codex CLI).
    OpenCode
}

/// <summary>What a schedule does when it fires. Stored as string — never rename.</summary>
public enum ScheduleKind
{
    /// <summary>Run an agent turn (optionally invoking a skill) in the schedule's conversation.</summary>
    Playbook,
    /// <summary>Open an autopilot session with the schedule's cap.</summary>
    AutopilotStart,
    /// <summary>Stop the project's open autopilot session, if any.</summary>
    AutopilotStop,
    /// <summary>One-shot, agent-authored: re-invoke the agent in its own conversation.</summary>
    Reminder
}

/// <summary>Outcome of one schedule firing. Stored as string — never rename.</summary>
public enum ScheduleRunStatus
{
    Running,
    Succeeded,
    Failed,
    /// <summary>Skipped without running: automation paused, budget blocked, agent missing.</summary>
    Skipped
}

/// <summary>What kind of principal produced an audit event. Stored as string — never rename.</summary>
public enum AuditActorKind
{
    Agent,
    User,
    System,
    ApiKey
}
