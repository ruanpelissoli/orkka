namespace Orkka.Modules.Agents.Features.AgentChat;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;

public sealed class ListMessagesEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    ITasksApi tasksApi,
    IDocumentsApi documentsApi,
    IBlobStorageService blobStorage)
    : EndpointWithoutRequest<MessageListResponse>
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    public override void Configure()
    {
        Get("/api/projects/{projectId}/agents/{agentId:ulid}/conversations/{conversationId:ulid}/messages");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var agentId = Ulid.Parse(Route<string>("agentId"));
        var conversationId = Ulid.Parse(Route<string>("conversationId"));

        var conversationExists = await db.AgentConversations
            .AsNoTracking()
            .AnyAsync(c => c.Id == conversationId && c.ProjectId == projectId && c.AgentId == agentId && c.OrgId == currentUser.OrgId, ct);

        if (!conversationExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Newest page first with a CreatedAt cursor: conversations grow without bound,
        // so the full history must never be a single response.
        var limit = Math.Clamp(Query<int?>("limit", isRequired: false) ?? DefaultPageSize, 1, MaxPageSize);
        var before = Query<DateTimeOffset?>("before", isRequired: false);

        var query = db.AgentMessages
            .AsNoTracking()
            .Where(m => m.OrgId == currentUser.OrgId && m.ConversationId == conversationId);

        if (before is { } cursor)
            query = query.Where(m => m.CreatedAt < cursor);

        var page = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        page.Reverse(); // chat order: oldest of the page first
        var messages = page;
        var messageIdsForLookups = messages.Select(m => m.Id).ToList();
        // List<Ulid>.Contains(nullable.Value) on a converter-mapped struct has failed to
        // translate (500: "Expression of type 'System.Ulid' cannot be used for parameter of
        // type 'Nullable<Ulid>'"). Both lookups below are already conversation-scoped, so
        // fetch by conversation and page-filter in memory instead.
        var messageIdSet = messageIdsForLookups.ToHashSet();

        // Attach diagrams extracted from architect messages so the chat can link to them
        var diagramsByMessage = (await db.ArchitectureDiagrams
            .AsNoTracking()
            .Where(d => d.OrgId == currentUser.OrgId && d.ConversationId == conversationId
                && d.MessageId != null)
            .OrderBy(d => d.CreatedAt)
            .Select(d => new { d.MessageId, d.Id, d.Title })
            .ToListAsync(ct))
            .Where(d => messageIdSet.Contains(d.MessageId!.Value))
            .GroupBy(d => d.MessageId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d => new MessageDiagramInfo(d.Id.ToString(), d.Title)).ToList());

        // Attach tasks created by the PM agent from chat messages (Tasks module owns them)
        var messageIds = messageIdsForLookups;
        var tasksByMessage = (await tasksApi.GetChatCreatedTasksAsync(projectId, messageIds, ct))
            .GroupBy(t => t.SourceMessageId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(t => new MessageTaskInfo(t.Id.ToString(), t.Number, t.Title)).ToList());

        // Attach documents saved from chat messages (Documents module owns them)
        var documentsByMessage = (await documentsApi.GetChatCreatedDocumentsAsync(projectId, messageIds, ct))
            .GroupBy(d => d.SourceMessageId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d => new MessageDocumentInfo(d.Id.ToString(), d.Title)).ToList());

        // Attach design assets generated from Designer messages — display URLs are signed
        // short-lived per response, never stored
        var designAssetRows = (await db.DesignAssets
            .AsNoTracking()
            .Where(a => a.OrgId == currentUser.OrgId && a.ConversationId == conversationId
                && a.MessageId != null)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct))
            .Where(a => messageIdSet.Contains(a.MessageId!.Value))
            .ToList();

        var designAssetsByMessage = new Dictionary<Ulid, List<MessageDesignAssetInfo>>();
        foreach (var asset in designAssetRows)
        {
            var url = await blobStorage.GenerateDownloadUrlAsync(
                asset.BlobContainer, asset.BlobName, DesignAssetGenerator.DisplayUrlExpiry);
            var info = new MessageDesignAssetInfo(
                asset.Id.ToString(), asset.Name, asset.Kind.ToString().ToLowerInvariant(), asset.Version, url);

            if (!designAssetsByMessage.TryGetValue(asset.MessageId!.Value, out var list))
                designAssetsByMessage[asset.MessageId.Value] = list = [];
            list.Add(info);
        }

        await Send.OkAsync(new MessageListResponse(
            messages
                .Select(m => m.ToResponse(
                    diagramsByMessage.GetValueOrDefault(m.Id),
                    tasksByMessage.GetValueOrDefault(m.Id),
                    documentsByMessage.GetValueOrDefault(m.Id),
                    designAssetsByMessage.GetValueOrDefault(m.Id)))
                .ToList(),
            hasMore), ct);
    }
}
