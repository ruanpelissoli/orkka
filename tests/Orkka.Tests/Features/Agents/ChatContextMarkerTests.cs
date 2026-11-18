namespace Orkka.Tests.Features.Agents;

using System.Text.Json;
using FluentAssertions;
using Orkka.Modules.Agents.Features.AgentChat;

/// <summary>Chat context linking: design blocks carry referenceAttachmentIds so uploaded
/// images can steer Gemini generation ("make it look like this").</summary>
public sealed class ChatContextMarkerTests
{
    [Fact]
    public void DesignBlock_ParsesReferenceAttachmentIds()
    {
        var payload = JsonSerializer.Serialize(new
        {
            assets = new[]
            {
                new
                {
                    kind = "image",
                    name = "avatar",
                    prompt = "match the attached style",
                    referenceAttachmentIds = new[] { "01HZZZZZZZZZZZZZZZZZZZZZZZ", "01HYYYYYYYYYYYYYYYYYYYYYYY" },
                },
            },
        });

        var blocks = DesignBlockParser.Parse($"```design\n{payload}\n```");

        blocks.Should().ContainSingle();
        var asset = blocks[0].Assets![0];
        asset.ReferenceAttachmentIds.Should().Equal(
            "01HZZZZZZZZZZZZZZZZZZZZZZZ", "01HYYYYYYYYYYYYYYYYYYYYYYY");
    }

    [Fact]
    public void DesignBlock_WithoutReferences_ParsesToNull()
    {
        var payload = JsonSerializer.Serialize(new
        {
            assets = new[] { new { kind = "svg", name = "logo", svg = "<svg viewBox=\"0 0 32 32\"></svg>" } },
        });

        var blocks = DesignBlockParser.Parse($"```design\n{payload}\n```");

        blocks[0].Assets![0].ReferenceAttachmentIds.Should().BeNull();
    }
}
