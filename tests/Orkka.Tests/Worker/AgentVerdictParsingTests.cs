namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;

public class AgentVerdictParsingTests
{
    private sealed class TestVerdict
    {
        public string? Decision { get; set; }
        public string? Summary { get; set; }
        public List<TestComment>? Comments { get; set; }
    }

    private sealed class TestComment
    {
        public string Path { get; set; } = "";
        public int Line { get; set; }
        public string Body { get; set; } = "";
    }

    private static TestVerdict? Parse(string? resultText, string? fullText = null) =>
        TaskOrchestrator.ParseAgentVerdict<TestVerdict>(resultText, fullText, v => v.Decision is not null);

    [Fact]
    public void ParsesCleanJsonResult()
    {
        var verdict = Parse("""{"decision": "approve", "summary": "LGTM"}""");

        verdict.Should().NotBeNull();
        verdict.Decision.Should().Be("approve");
    }

    [Fact]
    public void ParsesFencedJsonWithNarrationPrefix()
    {
        var result = "Now I have a complete picture. Let me produce my review.\n\n```json\n" +
            """{"decision": "request_changes", "summary": "Needs work"}""" + "\n```";

        var verdict = Parse(result);

        verdict.Should().NotBeNull();
        verdict.Decision.Should().Be("request_changes");
    }

    [Fact]
    public void RepairsUnescapedQuotesInsideStringValues()
    {
        // Models quote conversation text verbatim inside JSON strings — the exact
        // failure that discarded a real approve verdict (task 01KXPSZ2W5...).
        var result = """
            {"decision": "approve", "summary": "The PM explicitly replied "use .net 10 if possible." MonoGame supports it.", "comments": [{"path": "a.csproj", "line": 10, "body": "The reply was "use .net 10 if possible." — bump the TFM."}]}
            """;

        var verdict = Parse(result);

        verdict.Should().NotBeNull();
        verdict.Decision.Should().Be("approve");
        verdict.Summary.Should().Contain("use .net 10 if possible");
        verdict.Comments.Should().ContainSingle(c => c.Path == "a.csproj");
    }

    [Fact]
    public void FallsBackToFullStreamWhenResultIsProse()
    {
        var full = "Thinking about the diff...\n" +
            """{"decision": "approve", "summary": "Fine"}""" + "\nDone.";

        var verdict = Parse("I approve this PR.", full);

        verdict.Should().NotBeNull();
        verdict.Decision.Should().Be("approve");
    }

    [Fact]
    public void SkipsUnrelatedJsonInStream()
    {
        // An echoed tool call parses as an all-null verdict — the validity predicate
        // must keep scanning instead of accepting it.
        var full = """{"name": "Read", "arguments": {"file_path": "x"}}""" + "\n" +
            """{"decision": "approve", "summary": "OK"}""";

        var verdict = Parse(null, full);

        verdict.Should().NotBeNull();
        verdict.Decision.Should().Be("approve");
    }

    [Fact]
    public void ReturnsNullWhenNothingParses()
    {
        Parse("no json here", "still no json").Should().BeNull();
    }

    [Fact]
    public void RepairPreservesValidJson()
    {
        var json = """{"decision": "approve", "summary": "Escaped \"quotes\" and {braces} stay intact", "comments": []}""";

        TaskOrchestrator.RepairUnescapedQuotes(json).Should().Be(json);
    }

    [Fact]
    public void RepairEscapesQuoteFollowedByProse()
    {
        var broken = """{"summary": "He said "yes and no" while testing."}""";

        var repaired = TaskOrchestrator.RepairUnescapedQuotes(broken);

        repaired.Should().Be("""{"summary": "He said \"yes and no\" while testing."}""");
    }
}
