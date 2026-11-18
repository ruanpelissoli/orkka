namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// Ambient (org, user) identity of the human driving the current chat operation.
/// IChatAgentInvoker's surface carries no requester identity, and detached chat runs
/// have no ICurrentUser — so the two chat entry points stamp this AsyncLocal
/// (SendMessageEndpoint for workspace prep inside the request; ChatRunCoordinator's
/// detached run scope from the captured ChatTurnSpec) and the hosted hybrid routing
/// decorator reads it to decide cloud vs the requester's runner. Flows through awaits;
/// null means "no chat requester in scope" and routing falls back to cloud.
///
/// Lives in Contracts rather than beside the chat feature because it is the missing half of
/// IChatAgentInvoker, which is a Contracts type: any module that can call the invoker has to
/// be able to say who is calling. Briefing is the second such caller.
/// </summary>
public static class ChatRequesterContext
{
    private static readonly AsyncLocal<(Ulid OrgId, string UserId)?> Current = new();

    public static (Ulid OrgId, string UserId)? Get() => Current.Value;

    public static void Set(Ulid orgId, string userId) => Current.Value = (orgId, userId);

    public static void Clear() => Current.Value = null;
}
