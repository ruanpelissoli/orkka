namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Agents.Features.AgentChat;

public class DiagramBlockParserTests
{
    private const string SkeletonPayload =
        """
        {
          "type": "excalidraw",
          "version": 2,
          "source": "orkka-archie-skeleton",
          "name": "Call Flow",
          "elements": [
            { "id": "api", "type": "rectangle", "x": 120, "y": 80, "width": 200, "height": 80, "label": { "text": "API" } },
            { "type": "arrow", "x": 330, "y": 120, "width": 140, "height": 0, "start": { "id": "api" }, "end": { "id": "db" } }
          ]
        }
        """;

    private static string Fence(string language, string payload) =>
        $"```{language}\n{payload}\n```";

    [Fact]
    public void Parses_excalidraw_fence_and_takes_name_as_title()
    {
        var result = DiagramBlockParser.Parse($"Here it is.\n{Fence("excalidraw", SkeletonPayload)}\nDone.");

        result.SkipReasons.Should().BeEmpty();
        result.Blocks.Should().ContainSingle();
        result.Blocks[0].Title.Should().Be("Call Flow");
        result.Blocks[0].DiagramJson.Should().Contain("\"orkka-archie-skeleton\"");
    }

    [Fact]
    public void Falls_back_to_default_title_when_name_missing()
    {
        var payload = """{ "type": "excalidraw", "elements": [ { "type": "rectangle" } ] }""";

        var result = DiagramBlockParser.Parse(Fence("excalidraw", payload));

        result.Blocks.Should().ContainSingle();
        result.Blocks[0].Title.Should().Be("Architecture Diagram");
    }

    [Fact]
    public void Accepts_json_fence_that_self_identifies_as_excalidraw()
    {
        var result = DiagramBlockParser.Parse(Fence("json", SkeletonPayload));

        result.Blocks.Should().ContainSingle();
        result.SkipReasons.Should().BeEmpty();
    }

    [Fact]
    public void Ignores_ordinary_json_fence_without_skip_reason()
    {
        var result = DiagramBlockParser.Parse(Fence("json", """{ "config": true, "elements": [1] }"""));

        result.Blocks.Should().BeEmpty();
        result.SkipReasons.Should().BeEmpty();
    }

    [Fact]
    public void Ignores_other_code_fences()
    {
        var result = DiagramBlockParser.Parse("```csharp\nvar x = 1;\n```");

        result.Blocks.Should().BeEmpty();
        result.SkipReasons.Should().BeEmpty();
    }

    [Fact]
    public void Reports_malformed_json_in_excalidraw_fence()
    {
        var result = DiagramBlockParser.Parse(Fence("excalidraw", """{ "type": "excalidraw", "elements": [ }"""));

        result.Blocks.Should().BeEmpty();
        result.SkipReasons.Should().ContainSingle()
            .Which.Should().Contain("not valid JSON");
    }

    [Fact]
    public void Rejects_missing_elements_array()
    {
        var result = DiagramBlockParser.Parse(Fence("excalidraw", """{ "type": "excalidraw", "name": "X" }"""));

        result.Blocks.Should().BeEmpty();
        result.SkipReasons.Should().ContainSingle()
            .Which.Should().Contain("elements");
    }

    [Fact]
    public void Rejects_empty_elements_array()
    {
        var result = DiagramBlockParser.Parse(Fence("excalidraw", """{ "type": "excalidraw", "elements": [] }"""));

        result.Blocks.Should().BeEmpty();
        result.SkipReasons.Should().ContainSingle()
            .Which.Should().Contain("empty");
    }

    [Fact]
    public void Rejects_non_object_root_in_excalidraw_fence()
    {
        var result = DiagramBlockParser.Parse(Fence("excalidraw", """[ { "type": "rectangle" } ]"""));

        result.Blocks.Should().BeEmpty();
        result.SkipReasons.Should().ContainSingle()
            .Which.Should().Contain("expected a JSON object");
    }

    [Fact]
    public void Parses_multiple_blocks_in_one_message()
    {
        var second = """{ "type": "excalidraw", "name": "Second", "elements": [ { "type": "ellipse" } ] }""";
        var text = $"{Fence("excalidraw", SkeletonPayload)}\nand\n{Fence("excalidraw", second)}";

        var result = DiagramBlockParser.Parse(text);

        result.Blocks.Should().HaveCount(2);
        result.Blocks[0].Title.Should().Be("Call Flow");
        result.Blocks[1].Title.Should().Be("Second");
    }

    [Fact]
    public void Returns_empty_for_plain_text()
    {
        var result = DiagramBlockParser.Parse("No diagrams here, just prose.");

        result.Blocks.Should().BeEmpty();
        result.SkipReasons.Should().BeEmpty();
    }
}
