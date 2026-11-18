namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;

public class PlanContentParsingTests
{
    private const string PlanJson = """
        {"summary": "Add login page", "approach": "1. Create the page\n2. Wire auth", "filesChanged": [{"path": "app/login/page.tsx", "action": "create", "description": "New login page"}], "estimatedTokens": 0}
        """;

    [Fact]
    public void TryParsePlanContent_ParsesCleanJson()
    {
        var plan = TaskOrchestrator.TryParsePlanContent(PlanJson);

        plan.Should().NotBeNull();
        plan.Summary.Should().Be("Add login page");
        plan.FilesChanged.Should().ContainSingle(f => f.Path == "app/login/page.tsx");
    }

    [Fact]
    public void TryParsePlanContent_FindsPlanEmbeddedInNarration()
    {
        var content = $"I explored the codebase. Here is my plan:\n\n{PlanJson}\n\nLet me know if you approve.";

        var plan = TaskOrchestrator.TryParsePlanContent(content);

        plan.Should().NotBeNull();
        plan.Summary.Should().Be("Add login page");
    }

    [Fact]
    public void TryParsePlanContent_SkipsToolCallJsonAndFindsPlan()
    {
        var content = """{"name": "Read", "arguments": {"file_path": "/path/to/journal.jsonl"}}""" + "\n" + PlanJson;

        var plan = TaskOrchestrator.TryParsePlanContent(content);

        plan.Should().NotBeNull();
        plan.Approach.Should().Contain("Create the page");
    }

    [Fact]
    public void TryParsePlanContent_ReturnsNullForToolCallOnly()
    {
        var content = """{"name": "Read", "arguments": {"file_path": "/path/to/journal.jsonl"}}""";

        TaskOrchestrator.TryParsePlanContent(content).Should().BeNull();
    }

    [Fact]
    public void TryParsePlanContent_ReturnsNullForProse()
    {
        TaskOrchestrator.TryParsePlanContent("I will refactor the auth module step by step.").Should().BeNull();
    }

    [Fact]
    public void TryParsePlanContent_PrefersLastPlanWhenMultiple()
    {
        var first = """{"summary": "Draft", "approach": "old approach"}""";
        var second = """{"summary": "Final", "approach": "new approach"}""";

        var plan = TaskOrchestrator.TryParsePlanContent($"{first}\nActually, revised:\n{second}");

        plan.Should().NotBeNull();
        plan.Summary.Should().Be("Final");
    }

    [Fact]
    public void FormatPlanForPrBody_RendersMarkdownNotJson()
    {
        var body = TaskOrchestrator.FormatPlanForPrBody(PlanJson);

        body.Should().StartWith("Add login page");
        body.Should().Contain("1. Create the page");
        body.Should().Contain("### Planned file changes");
        body.Should().Contain("- `app/login/page.tsx` (create) — New login page");
        body.Should().NotContain("\"summary\"");
    }

    [Fact]
    public void FormatPlanForPrBody_TruncatesLongApproach()
    {
        var longApproach = new string('x', 20_000);
        var json = $$"""{"summary": "Big plan", "approach": "{{longApproach}}"}""";

        var body = TaskOrchestrator.FormatPlanForPrBody(json);

        body.Length.Should().BeLessThan(15_000);
        body.Should().Contain("(truncated)");
    }

    [Fact]
    public void FormatPlanForPrBody_FallsBackToCodeFenceOnInvalidJson()
    {
        var body = TaskOrchestrator.FormatPlanForPrBody("not json at all {");

        body.Should().StartWith("```json");
        body.Should().Contain("not json at all {");
    }

    [Fact]
    public void FormatPlanForPrBody_HandlesMissingOptionalFields()
    {
        var body = TaskOrchestrator.FormatPlanForPrBody("""{"summary": "Only summary"}""");

        body.Should().Be("Only summary");
    }

    [Fact]
    public void StripToolCallArtifacts_RemovesToolCallsKeepsProse()
    {
        var content = """
            I'll start by reading the journal.
            {"name": "Read", "arguments": {"file_path": "/path/to/journal.jsonl"}}
            The plan is to add markdown rendering to the plan viewer.
            """;

        var cleaned = TaskOrchestrator.StripToolCallArtifacts(content);

        cleaned.Should().NotContain("journal.jsonl");
        cleaned.Should().Contain("I'll start by reading the journal.");
        cleaned.Should().Contain("markdown rendering");
    }

    [Fact]
    public void StripToolCallArtifacts_KeepsNonToolCallJson()
    {
        var content = """Config: {"retries": 3} should stay.""";

        TaskOrchestrator.StripToolCallArtifacts(content).Should().Be(content);
    }

    [Fact]
    public void StripToolCallArtifacts_HandlesBracesInsideStrings()
    {
        var content = """
            {"name": "Bash", "arguments": {"command": "echo '}{'"}}
            Real plan text.
            """;

        TaskOrchestrator.StripToolCallArtifacts(content).Should().Be("Real plan text.");
    }
}
