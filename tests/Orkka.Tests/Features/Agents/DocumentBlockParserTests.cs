namespace Orkka.Tests.Features.Agents;

using System.Text.Json;
using FluentAssertions;
using Orkka.Modules.Agents.Features.AgentChat;

public class DocumentBlockParserTests
{
    private static string Block(object payload) =>
        $"```document\n{JsonSerializer.Serialize(payload)}\n```";

    [Fact]
    public void Parses_simple_document_block()
    {
        var text = $"Here you go.\n{Block(new { documents = new[] { new { title = "T", path = "decisions", content = "# Hi" } } })}\nSaved!";

        var blocks = DocumentBlockParser.Parse(text);

        blocks.Should().ContainSingle();
        var documents = blocks[0].Documents;
        documents.Should().ContainSingle();
        documents![0].Title.Should().Be("T");
        documents[0].Path.Should().Be("decisions");
        documents[0].Content.Should().Be("# Hi");
    }

    [Fact]
    public void Salvages_the_complete_documents_of_a_block_the_output_limit_cut_off()
    {
        // 2026-09-07: a 26-document consolidation was cut at 64K output tokens and the turn
        // saved nothing. The complete documents before the cut are recoverable — and the
        // one the cut landed in is dropped, never guessed at.
        var whole = JsonSerializer.Serialize(new { documents = new[]
        {
            new { title = "One", path = "a", content = "# One\n\n```bash\necho hi\n```" },
            new { title = "Two", path = "a/b", content = "# Two with a \"quote\" and a \\ backslash" },
            new { title = "Three", path = "", content = "# Three — this one gets cut" },
        } });
        var cut = whole[..whole.IndexOf("gets cut", StringComparison.Ordinal)];
        var text = $"Here is the knowledge base.\n```document\n{cut}";

        var blocks = DocumentBlockParser.Parse(text, out var salvaged);

        salvaged.Should().Be(2);
        blocks.Should().ContainSingle();
        blocks[0].Documents.Should().HaveCount(2);
        blocks[0].Documents![0].Title.Should().Be("One");
        blocks[0].Documents![1].Content.Should().Contain("\"quote\"");
    }

    [Fact]
    public void A_marker_quoted_inside_a_document_is_not_a_block()
    {
        // A knowledge base that quotes the project's own docs ("via ```design blocks",
        // "end with a ```document block") carries the markers INSIDE JSON strings. Only a
        // marker that opens a line is a block; a terminated block still parses around them.
        var content = "Agents create assets via ```design blocks and documents via ```document blocks.";
        var text = $"Saved.\n{Block(new { documents = new[] { new { title = "Rules", path = "", content } } })}\nDone.";

        var blocks = DocumentBlockParser.Parse(text, out var salvaged);

        salvaged.Should().Be(0);
        blocks.Should().ContainSingle();
        blocks[0].Documents![0].Content.Should().Be(content);
        FencedBlockMarkers.IndexOf("x ```document\n{}", "```document", 0).Should().Be(-1);
        FencedBlockMarkers.IndexOf("```documents\n{}", "```document", 0).Should().Be(-1, "```documents is a different fence");
        FencedBlockMarkers.IndexOf("intro\n```document  \n{}", "```document", 0).Should().Be(6);
    }

    [Fact]
    public void Parses_document_whose_content_contains_code_fences()
    {
        // The regression that motivated this parser: markdown content with its own ``` fences
        // inside the JSON payload must not terminate the block early.
        var content = "# Setup\n\n```bash\nnpm install\n```\n\nDone.\n\n```csharp\nvar x = 1;\n```";
        var text = $"Intro.\n{Block(new { documents = new[] { new { title = "Onboarding", path = "", content } } })}\nConfirmed.";

        var blocks = DocumentBlockParser.Parse(text);

        blocks.Should().ContainSingle();
        blocks[0].Documents![0].Content.Should().Be(content);
    }

    [Fact]
    public void Parses_multiple_blocks_in_one_message()
    {
        var text =
            Block(new { documents = new[] { new { title = "A", path = "", content = "a" } } }) +
            "\nand another\n" +
            Block(new { documents = new[] { new { title = "B", path = "", content = "```code```" } } });

        var blocks = DocumentBlockParser.Parse(text);

        blocks.Should().HaveCount(2);
        blocks[0].Documents![0].Title.Should().Be("A");
        blocks[1].Documents![0].Title.Should().Be("B");
    }

    [Fact]
    public void Returns_empty_for_text_without_blocks()
    {
        DocumentBlockParser.Parse("Just a normal reply with ```csharp\ncode\n``` in it.")
            .Should().BeEmpty();
    }

    [Fact]
    public void Skips_unterminated_block()
    {
        DocumentBlockParser.Parse("```document\n{\"documents\": [{\"title\": \"cut off")
            .Should().BeEmpty();
    }

    [Fact]
    public void Skips_invalid_json_but_still_finds_later_valid_block()
    {
        var text =
            "```document\nnot json at all\n```\n" +
            Block(new { documents = new[] { new { title = "Valid", path = "", content = "x" } } });

        var blocks = DocumentBlockParser.Parse(text);

        blocks.Should().ContainSingle();
        blocks[0].Documents![0].Title.Should().Be("Valid");
    }
}
