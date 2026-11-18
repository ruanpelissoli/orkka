namespace Orkka.Tests.Features.Tasks;

using System.Text.Json.Serialization;
using Orkka.SharedKernel.Ai;
using FluentAssertions;

/// <summary>
/// "Return ONLY valid JSON" is a request, not a guarantee. These pin the shapes that actually
/// come back — and the shapes that must fail loudly rather than as "rephrase your goal".
/// </summary>
public sealed class ModelJsonTests
{
    private sealed class Plan
    {
        [JsonPropertyName("milestoneTitle")]
        public string? MilestoneTitle { get; set; }

        [JsonPropertyName("tasks")]
        public List<PlanTask>? Tasks { get; set; }
    }

    private sealed class PlanTask
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";
    }

    [Fact]
    public void Parses_bare_json()
    {
        var plan = ModelJson.Deserialize<Plan>(
            """{"milestoneTitle":"Mobile scaffold","tasks":[{"title":"Set up Expo"}]}""", "plan");

        plan.MilestoneTitle.Should().Be("Mobile scaffold");
        plan.Tasks.Should().ContainSingle();
    }

    [Fact]
    public void Parses_json_inside_a_fenced_block()
    {
        var plan = ModelJson.Deserialize<Plan>(
            "```json\n{\"milestoneTitle\":\"Fenced\",\"tasks\":[{\"title\":\"A\"}]}\n```", "plan");

        plan.MilestoneTitle.Should().Be("Fenced");
    }

    [Fact]
    public void Parses_json_with_a_preamble_and_trailing_commentary()
    {
        // The single most common real failure: the model explains itself first.
        var plan = ModelJson.Deserialize<Plan>(
            "Here's the plan:\n\n{\"milestoneTitle\":\"Chatty\",\"tasks\":[{\"title\":\"A\"}]}\n\n" +
            "Let me know if you'd like me to adjust it.", "plan");

        plan.MilestoneTitle.Should().Be("Chatty");
    }

    [Fact]
    public void Braces_inside_strings_do_not_end_the_object_early()
    {
        var plan = ModelJson.Deserialize<Plan>(
            """{"milestoneTitle":"Uses {braces} and \"quotes\"","tasks":[{"title":"A"}]}""", "plan");

        plan.MilestoneTitle.Should().Be("Uses {braces} and \"quotes\"");
        plan.Tasks.Should().ContainSingle();
    }

    [Fact]
    public void Truncated_output_is_reported_as_truncation()
    {
        // What an output-cap hit looks like: the object never closes. Previously this surfaced
        // as a generic parse error and, above it, as "try rephrasing the goal".
        var act = () => ModelJson.Deserialize<Plan>(
            """{"milestoneTitle":"Cut off","tasks":[{"title":"Half a ta""", "plan");

        act.Should().Throw<ModelOutputException>()
            .WithMessage("*cut off*");
    }

    [Fact]
    public void Prose_with_no_json_at_all_throws_with_the_raw_output_attached()
    {
        var act = () => ModelJson.Deserialize<Plan>(
            "I can't plan this without knowing which repo to use.", "plan");

        act.Should().Throw<ModelOutputException>()
            .Which.RawOutput.Should().Contain("which repo");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_output_throws_rather_than_returning_null(string? text)
    {
        var act = () => ModelJson.Deserialize<Plan>(text, "plan");

        act.Should().Throw<ModelOutputException>();
    }

    [Fact]
    public void Trailing_commas_are_tolerated()
    {
        var plan = ModelJson.Deserialize<Plan>(
            """{"milestoneTitle":"Sloppy","tasks":[{"title":"A"},],}""", "plan");

        plan.Tasks.Should().ContainSingle();
    }

    [Fact]
    public void ExtractArray_returns_a_bare_array_unchanged()
    {
        ModelJson.ExtractArray("""[{"title":"A"}]""")
            .Should().Be("""[{"title":"A"}]""");
    }

    [Fact]
    public void ExtractArray_unwraps_a_fenced_array_with_trailing_commentary()
    {
        // The production failure shape: fence + prose after the closing fence, which a
        // strip-the-fences approach leaves as "[...]```prose" and the deserializer rejects
        // with "'`' is invalid after a single JSON value".
        var text = "```json\n[{\"dependsOnTaskId\":\"01ABC\",\"reason\":\"needs the endpoint\"}]\n```\n" +
                   "No other dependencies were found.";

        ModelJson.ExtractArray(text)
            .Should().Be("""[{"dependsOnTaskId":"01ABC","reason":"needs the endpoint"}]""");
    }

    [Fact]
    public void ExtractArray_ignores_brackets_inside_strings()
    {
        var text = """[{"reason":"uses [square] brackets and a ] there"}]""";

        ModelJson.ExtractArray(text).Should().Be(text);
    }

    [Fact]
    public void ExtractArray_returns_null_for_a_truncated_array()
    {
        ModelJson.ExtractArray("""[{"reason":"cut off mid-obj""").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No dependencies here, just prose.")]
    public void ExtractArray_returns_null_when_there_is_no_array(string? text)
    {
        ModelJson.ExtractArray(text).Should().BeNull();
    }
}
