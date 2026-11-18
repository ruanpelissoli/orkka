namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;

public class MergeResolutionParsingTests
{
    [Fact]
    public void ParseFileChoices_ReadsCamelCaseSnapshotChoices()
    {
        const string json = """
            {"files":[{"path":"go.mod","detail":"add/add","choice":"task"},{"path":"config.go","detail":"","choice":"ai"},{"path":"readme.md","detail":"","choice":null}],"summary":"x","detectedAt":"2026-07-15T00:00:00Z"}
            """;

        var choices = TaskOrchestrator.ParseFileChoices(json);

        choices.Should().NotBeNull();
        choices.Should().HaveCount(2);
        choices!["go.mod"].Should().Be("task");
        choices["config.go"].Should().Be("ai");
        choices.Should().NotContainKey("readme.md");
    }

    [Fact]
    public void ParseFileChoices_IgnoresUnknownChoiceValues()
    {
        const string json = """
            {"files":[{"path":"a.ts","detail":"","choice":"bogus"}],"summary":null,"detectedAt":"2026-07-15T00:00:00Z"}
            """;

        TaskOrchestrator.ParseFileChoices(json).Should().BeNull();
    }

    [Fact]
    public void ParseFileChoices_NullOrInvalidJson_ReturnsNull()
    {
        TaskOrchestrator.ParseFileChoices(null).Should().BeNull();
        TaskOrchestrator.ParseFileChoices("").Should().BeNull();
        TaskOrchestrator.ParseFileChoices("not json").Should().BeNull();
    }
}
