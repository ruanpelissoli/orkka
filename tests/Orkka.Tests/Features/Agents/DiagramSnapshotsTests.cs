namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;

public class DiagramSnapshotsTests
{
    private static ArchitectureDiagram Diagram() => new()
    {
        Id = Ulid.NewUlid(),
        OrgId = Ulid.NewUlid(),
        ProjectId = Ulid.NewUlid(),
        AgentId = Ulid.NewUlid(),
        ConversationId = Ulid.NewUlid(),
        Title = "System Overview",
        Type = DiagramType.SystemArchitecture,
        DiagramData = """{"type":"excalidraw","elements":[]}""",
        Version = 2,
        Description = "High level",
    };

    [Fact]
    public void Snapshot_carries_diagram_state_and_user_actor()
    {
        var diagram = Diagram();

        var version = DiagramSnapshots.FromCurrentState(
            diagram, changedByUserId: "user_123", changedByAgentId: null);

        version.DiagramId.Should().Be(diagram.Id);
        version.OrgId.Should().Be(diagram.OrgId);
        version.Version.Should().Be(2);
        version.Title.Should().Be("System Overview");
        version.Description.Should().Be("High level");
        version.DiagramData.Should().Be(diagram.DiagramData);
        version.ChangedByUserId.Should().Be("user_123");
        version.ChangedByAgentId.Should().BeNull();
        version.RequestedByUserId.Should().BeNull();
        version.SourceConversationId.Should().BeNull();
        version.SourceMessageId.Should().BeNull();
    }

    [Fact]
    public void Snapshot_records_requester_and_chat_source_for_agent_versions()
    {
        var diagram = Diagram();
        var messageId = Ulid.NewUlid();

        var version = DiagramSnapshots.FromCurrentState(
            diagram, changedByUserId: null, changedByAgentId: diagram.AgentId,
            requestedByUserId: "user_asker",
            sourceConversationId: diagram.ConversationId,
            sourceMessageId: messageId);

        version.ChangedByUserId.Should().BeNull();
        version.ChangedByAgentId.Should().Be(diagram.AgentId);
        version.RequestedByUserId.Should().Be("user_asker");
        version.SourceConversationId.Should().Be(diagram.ConversationId);
        version.SourceMessageId.Should().Be(messageId);
    }
}
