namespace Orkka.Tests.Features.Agents;

using System.Text.Json;
using FluentAssertions;
using Orkka.Modules.Agents.Features.AgentChat;

public class DesignBlockParserTests
{
    private static string Block(object payload) =>
        $"```design\n{JsonSerializer.Serialize(payload)}\n```";

    [Fact]
    public void Parses_svg_asset()
    {
        var text = $"Here's your logo.\n{Block(new { assets = new[] { new { kind = "svg", name = "logo-icon", svg = "<svg viewBox=\"0 0 32 32\"></svg>" } } })}\nReady for review.";

        var blocks = DesignBlockParser.Parse(text);

        blocks.Should().ContainSingle();
        var assets = blocks[0].Assets;
        assets.Should().ContainSingle();
        assets![0].Kind.Should().Be("svg");
        assets[0].Name.Should().Be("logo-icon");
        assets[0].Svg.Should().StartWith("<svg");
    }

    [Fact]
    public void Parses_image_asset_with_aspect_ratio_and_parent()
    {
        var text = Block(new
        {
            assets = new[] { new { kind = "image", name = "hero", prompt = "a hero image", aspectRatio = "16:9", basedOnAssetId = "01HZZZZZZZZZZZZZZZZZZZZZZZ" } }
        });

        var blocks = DesignBlockParser.Parse(text);

        blocks.Should().ContainSingle();
        var asset = blocks[0].Assets![0];
        asset.Kind.Should().Be("image");
        asset.Prompt.Should().Be("a hero image");
        asset.AspectRatio.Should().Be("16:9");
        asset.BasedOnAssetId.Should().Be("01HZZZZZZZZZZZZZZZZZZZZZZZ");
    }

    [Fact]
    public void A_design_marker_inside_a_json_string_is_not_a_design_block()
    {
        // The document consolidation that quoted CLAUDE.md ("via ```design blocks") must not
        // be read as a design block — that is what put "Creating designs…" under a documents
        // reply and a design diagnostic on JSON the designer never wrote.
        var text = "```document\n{\"documents\":[{\"title\":\"t\",\"path\":\"\",\"content\":\"Muse creates assets via ```design blocks.\"}]}\n```";

        var blocks = DesignBlockParser.Parse(text, out var diagnostics);

        blocks.Should().BeEmpty();
        diagnostics.Should().BeNull();
    }

    [Fact]
    public void Parses_gif_asset_with_frames()
    {
        var text = Block(new
        {
            assets = new[]
            {
                new
                {
                    kind = "gif",
                    name = "loading-dots",
                    frameDelayMs = 120,
                    frames = new[] { new { prompt = "frame 1" }, new { prompt = "frame 2" }, new { prompt = "frame 3" } },
                }
            }
        });

        var blocks = DesignBlockParser.Parse(text);

        blocks.Should().ContainSingle();
        var asset = blocks[0].Assets![0];
        asset.Kind.Should().Be("gif");
        asset.FrameDelayMs.Should().Be(120);
        asset.Frames.Should().HaveCount(3);
        asset.Frames![2].Prompt.Should().Be("frame 3");
    }

    [Fact]
    public void Parses_asset_whose_prompt_contains_code_fences()
    {
        // SVG markup and prompts routinely contain ``` sequences — the block must not
        // terminate at an inner fence.
        var prompt = "Render the text ```npm install``` in monospace on a dark banner";
        var text = $"Sure.\n{Block(new { assets = new[] { new { kind = "image", name = "code-banner", prompt } } })}";

        var blocks = DesignBlockParser.Parse(text);

        blocks.Should().ContainSingle();
        blocks[0].Assets![0].Prompt.Should().Be(prompt);
    }

    [Fact]
    public void Returns_empty_for_text_without_blocks()
    {
        DesignBlockParser.Parse("A normal reply with ```csharp\ncode\n``` in it.")
            .Should().BeEmpty();
    }

    [Fact]
    public void Skips_unterminated_block()
    {
        DesignBlockParser.Parse("```design\n{\"assets\": [{\"kind\": \"svg")
            .Should().BeEmpty();
    }

    [Fact]
    public void Skips_invalid_json_but_still_finds_later_valid_block()
    {
        var text =
            "```design\nnot json\n```\n" +
            Block(new { assets = new[] { new { kind = "svg", name = "ok", svg = "<svg/>" } } });

        var blocks = DesignBlockParser.Parse(text);

        blocks.Should().ContainSingle();
        blocks[0].Assets![0].Name.Should().Be("ok");
    }

    [Fact]
    public void Does_not_confuse_design_and_document_markers()
    {
        var text =
            "```document\n{\"documents\": [{\"title\": \"T\", \"path\": \"\", \"content\": \"x\"}]}\n```";

        DesignBlockParser.Parse(text).Should().BeEmpty();
    }

    // ── Lenient parsing of observed model schema drift ──────────────────────

    [Fact]
    public void Numeric_frames_count_becomes_that_many_copies_of_the_prompt()
    {
        var text = Block(new
        {
            assets = new[] { new { kind = "gif", name = "idle", prompt = "hero idle loop", frames = 4 } }
        });

        var asset = DesignBlockParser.Parse(text)[0].Assets![0];

        asset.Frames.Should().HaveCount(4);
        asset.Frames!.Should().AllSatisfy(f => f.Prompt.Should().Be("hero idle loop"));
    }

    [Fact]
    public void Numeric_frames_count_is_clamped_to_the_2_to_8_range()
    {
        var text = Block(new
        {
            assets = new[] { new { kind = "gif", name = "long", prompt = "p", frames = 30 } }
        });

        DesignBlockParser.Parse(text)[0].Assets![0].Frames.Should().HaveCount(8);
    }

    [Fact]
    public void Frames_as_plain_strings_become_frame_prompts()
    {
        var text = Block(new
        {
            assets = new[] { new { kind = "gif", name = "dots", frames = new[] { "one dot", "two dots" } } }
        });

        var asset = DesignBlockParser.Parse(text)[0].Assets![0];

        asset.Frames.Should().HaveCount(2);
        asset.Frames![1].Prompt.Should().Be("two dots");
    }

    [Fact]
    public void Fps_is_honored_as_frame_delay()
    {
        var text = Block(new
        {
            assets = new[] { new { kind = "gif", name = "idle", prompt = "p", frames = 2, fps = 8 } }
        });

        DesignBlockParser.Parse(text)[0].Assets![0].FrameDelayMs.Should().Be(125);
    }

    [Fact]
    public void Width_and_height_map_to_the_nearest_documented_aspect_ratio()
    {
        var text = Block(new
        {
            assets = new[]
            {
                new { kind = "image", name = "strip", prompt = "p", width = 768, height = 256 },
                new { kind = "image", name = "portrait", prompt = "p", width = 192, height = 256 },
            }
        });

        var assets = DesignBlockParser.Parse(text)[0].Assets!;

        assets[0].EffectiveAspectRatio.Should().Be("21:9");
        assets[1].EffectiveAspectRatio.Should().Be("3:4");
    }

    [Fact]
    public void Explicit_aspect_ratio_wins_over_width_and_height()
    {
        var text = Block(new
        {
            assets = new[] { new { kind = "image", name = "x", prompt = "p", aspectRatio = "1:1", width = 768, height = 256 } }
        });

        DesignBlockParser.Parse(text)[0].Assets![0].EffectiveAspectRatio.Should().Be("1:1");
    }

    [Fact]
    public void One_malformed_asset_no_longer_discards_its_valid_siblings()
    {
        // referenceAttachmentIds as a number still defeats deserialization — the bad
        // asset is salvaged down to name/kind (the generator fails it visibly) and the
        // valid sibling survives.
        var text = Block(new
        {
            assets = new object[]
            {
                new { kind = "image", name = "good", prompt = "a fine prompt" },
                new { kind = "image", name = "bad", prompt = "p", referenceAttachmentIds = 5 },
            }
        });

        var assets = DesignBlockParser.Parse(text)[0].Assets!;

        assets.Should().HaveCount(2);
        assets[0].Prompt.Should().Be("a fine prompt");
        assets[1].Name.Should().Be("bad");
        assets[1].Kind.Should().Be("image");
    }

    [Fact]
    public void The_real_sprite_failure_block_now_yields_all_three_assets()
    {
        // Regression: the exact drift Sprite emitted on 2026-07-20 — numeric "frames",
        // "fps", pixel "width"/"height", extra "transparent" — used to kill the whole
        // block including its two valid image assets.
        var text = Block(new
        {
            assets = new object[]
            {
                new { kind = "image", name = "strip-south", description = "d", prompt = "p1", width = 768, height = 256, transparent = true },
                new { kind = "image", name = "strip-east", description = "d", prompt = "p2", width = 768, height = 256, transparent = true },
                new { kind = "gif", name = "idle-preview", description = "d", prompt = "p3", width = 192, height = 256, frames = 4, fps = 8, transparent = true },
            }
        });

        var assets = DesignBlockParser.Parse(text, out var diagnostics)[0].Assets!;

        diagnostics.Should().BeNull();
        assets.Should().HaveCount(3);
        assets[0].EffectiveAspectRatio.Should().Be("21:9");
        assets[2].Frames.Should().HaveCount(4);
        assets[2].FrameDelayMs.Should().Be(125);
    }

    [Fact]
    public void Diagnostics_explain_an_unparseable_block()
    {
        DesignBlockParser.Parse("```design\nnot json\n```", out var diagnostics).Should().BeEmpty();

        diagnostics.Should().NotBeNull();
    }

    [Fact]
    public void ParseBlockJson_normalizes_drift_and_reports_invalid_payloads()
    {
        var block = DesignBlockParser.ParseBlockJson(
            """{"assets":[{"kind":"gif","name":"g","prompt":"p","frames":3}]}""", out var ok);
        ok.Should().BeNull();
        block!.Assets![0].Frames.Should().HaveCount(3);

        DesignBlockParser.ParseBlockJson("not json", out var bad).Should().BeNull();
        bad.Should().NotBeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 100)]
    public void NearestAspectRatio_requires_both_dimensions(int? width, int? height)
    {
        DesignAssetSpec.NearestAspectRatio(width, height).Should().BeNull();
    }
}
