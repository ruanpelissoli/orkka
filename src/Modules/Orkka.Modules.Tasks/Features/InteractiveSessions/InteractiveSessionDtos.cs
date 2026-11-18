namespace Orkka.Modules.Tasks.Features.InteractiveSessions;

/// <summary>
/// Opens a fresh interactive session. Deliberately small: everything else about the task is
/// either defaulted (priority, type) or derived (execution target from the caller's preference),
/// because a session exists for work that was not worth filling in a form for. The task can be
/// edited afterwards like any other.
/// </summary>
public sealed record StartInteractiveSessionRequest(
    Ulid RepoId,
    // Null/blank gets a placeholder — the user should be able to start typing about the bug
    // before they have to name it.
    string? Title = null,
    // Pin the session to a specific dev agent (null = auto-assign by role/repo at first turn).
    Ulid? AssignedAgentId = null,
    // Run the agent on this model instead of its own (null/empty = the agent's own).
    string? Model = null);
