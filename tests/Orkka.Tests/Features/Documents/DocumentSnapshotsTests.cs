namespace Orkka.Tests.Features.Documents;

using FluentAssertions;
using Orkka.Modules.Documents.Domain;

public class DocumentSnapshotsTests
{
    private static DocumentEntity Document() => new()
    {
        Id = Ulid.NewUlid(),
        OrgId = Ulid.NewUlid(),
        ProjectId = Ulid.NewUlid(),
        Title = "Spec",
        Path = "docs",
        Content = "# Spec\nBody",
        CurrentVersion = 3,
    };

    [Fact]
    public void Snapshot_carries_document_state_and_actor()
    {
        var document = Document();

        var revision = DocumentSnapshots.FromCurrentState(
            document, changedByUserId: "user_123", changedByAgentId: null);

        revision.DocumentId.Should().Be(document.Id);
        revision.OrgId.Should().Be(document.OrgId);
        revision.Version.Should().Be(3);
        revision.Title.Should().Be("Spec");
        revision.Path.Should().Be("docs");
        revision.Content.Should().Be("# Spec\nBody");
        revision.ChangedByUserId.Should().Be("user_123");
        revision.ChangedByAgentId.Should().BeNull();
        revision.RequestedByUserId.Should().BeNull();
        revision.SourceConversationId.Should().BeNull();
        revision.SourceMessageId.Should().BeNull();
    }

    [Fact]
    public void Snapshot_records_requester_and_chat_source_for_agent_revisions()
    {
        var document = Document();
        var agentId = Ulid.NewUlid();
        var conversationId = Ulid.NewUlid();
        var messageId = Ulid.NewUlid();

        var revision = DocumentSnapshots.FromCurrentState(
            document, changedByUserId: null, changedByAgentId: agentId,
            requestedByUserId: "user_asker",
            sourceConversationId: conversationId,
            sourceMessageId: messageId);

        revision.ChangedByUserId.Should().BeNull();
        revision.ChangedByAgentId.Should().Be(agentId);
        revision.RequestedByUserId.Should().Be("user_asker");
        revision.SourceConversationId.Should().Be(conversationId);
        revision.SourceMessageId.Should().Be(messageId);
    }
}
