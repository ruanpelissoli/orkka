namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Agents.Domain;

/// <summary>
/// A designer that can only create will re-imagine the product's own identity every time it
/// is asked for something. Muse had the project's palette and nothing else: asked for a
/// marketing image, it invented a mascot from the words describing one, and the result was
/// recognisably not the brand — "we have all the icons in frontend app and on the design page
/// for you to use and you created something that not even close to Orkka branding"
/// (2026-09-08). An identity has to be read, not recalled, so the prompt sends the agent to
/// look first and the tools give it something to look at.
/// </summary>
public sealed class DesignerGroundingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_agent_is_told_to_look_before_it_draws(bool toolsEnabled)
    {
        var block = DefaultPrompts.DesignerGrounding(toolsEnabled);

        block.Should().Contain("LOOK AT WHAT ALREADY EXISTS");
        block.Should().Contain("Design page");
        block.Should().Contain("repositories in your workspace",
            "artwork the app already ships is the most reliable answer to what it looks like");
    }

    [Fact]
    public void With_tools_it_is_pointed_at_them_by_name()
    {
        var block = DefaultPrompts.DesignerGrounding(toolsEnabled: true);

        block.Should().Contain("list_design_assets").And.Contain("get_design_asset");
        block.Should().Contain("reuse that geometry exactly",
            "redrawing a mark from its description is exactly the failure this prevents");
    }

    [Fact]
    public void Without_tools_it_asks_rather_than_guesses()
    {
        var block = DefaultPrompts.DesignerGrounding(toolsEnabled: false);

        block.Should().NotContain("list_design_assets", "the turn has no tools to call");
        block.Should().Contain("attach");
    }

    /// <summary>
    /// Four-space indentation turns a prompt block into a Markdown code fence, which the model
    /// reads as an example rather than an instruction. Raw-string interpolation does NOT
    /// re-indent a multi-line value — only its first line lands where it was written — so a
    /// block assembled by interpolation silently acquires exactly this defect.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_line_is_indented_far_enough_to_read_as_a_code_block(bool toolsEnabled)
    {
        var indented = DefaultPrompts.DesignerGrounding(toolsEnabled)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("    ", StringComparison.Ordinal))
            .ToList();

        indented.Should().BeEmpty();
    }
}
