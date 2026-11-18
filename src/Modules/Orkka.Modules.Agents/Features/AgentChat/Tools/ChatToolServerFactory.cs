namespace Orkka.Modules.Agents.Features.AgentChat.Tools;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Builds the same <see cref="OrkkaChatToolServer"/> the in-process chat path builds, for a
/// caller outside this module: the Worker's chat relay, where a turn routed to the user's own
/// machine has its tools answered.
///
/// The relayed turn's tool server MUST live on a host with the database — the runner has
/// neither — which is why this exists at all. Everything else is identical to the cloud path:
/// same handlers, same role gating, same emitted payloads.
/// </summary>
internal sealed class ChatToolServerFactory(
    IServiceScopeFactory scopeFactory,
    ILogger<ChatToolServerFactory> logger) : IChatToolServerFactory
{
    public IChatToolServer Create(ChatToolServerContext context, Action<object> emit) =>
        new OrkkaChatToolServer(
            new ChatTurnToolContext
            {
                OrgId = context.OrgId,
                ProjectId = context.ProjectId,
                AgentId = context.AgentId,
                ConversationId = context.ConversationId,
                AssistantMessageId = context.AssistantMessageId,
                RequesterUserId = context.RequesterUserId,
                DesignFolderPath = context.DesignFolderPath,
                RequesterLocale = context.RequesterLocale,
                AgentRole = context.AgentRole,
                Emit = emit,
            },
            scopeFactory,
            logger);
}
