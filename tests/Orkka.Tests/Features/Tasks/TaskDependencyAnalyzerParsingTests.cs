namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Modules.Tasks.Infrastructure.Services;

/// <summary>
/// The dependency prompt says "no markdown fences", but production replies arrive fenced or
/// explained anyway. Parsing must survive that: a throw here silently degrades an Autopilot
/// board to "no dependencies detected", removing its entire ordering.
/// </summary>
public sealed class TaskDependencyAnalyzerParsingTests
{
    private static readonly NullLogger Logger = NullLogger.Instance;

    [Fact]
    public void Parses_a_bare_json_array()
    {
        var deps = TaskDependencyAnalyzer.ParseDependencies(
            """[{"dependsOnTaskId":"01HZX","reason":"needs the endpoint"}]""", Logger);

        deps.Should().ContainSingle();
        deps![0].DependsOnTaskId.Should().Be("01HZX");
        deps[0].Reason.Should().Be("needs the endpoint");
    }

    [Fact]
    public void Parses_a_fenced_payload()
    {
        var deps = TaskDependencyAnalyzer.ParseDependencies(
            "```json\n[{\"dependsOnTaskId\":\"01HZX\",\"reason\":\"needs the schema\"}]\n```", Logger);

        deps.Should().ContainSingle();
        deps![0].DependsOnTaskId.Should().Be("01HZX");
    }

    [Fact]
    public void Parses_a_fenced_payload_with_trailing_commentary()
    {
        // The exact shape behind the 2026-08-24 production failure: after stripping the opening
        // fence, "[...]```prose" reached the deserializer, which threw
        // "'`' is invalid after a single JSON value".
        var deps = TaskDependencyAnalyzer.ParseDependencies(
            "```json\n[{\"dependsOnTaskId\":\"01HZX\",\"reason\":\"calls POST /orders\"}]\n```\n" +
            "The other tasks are unrelated to this one.", Logger);

        deps.Should().ContainSingle();
        deps![0].Reason.Should().Be("calls POST /orders");
    }

    [Fact]
    public void Parses_a_payload_with_a_preamble()
    {
        var deps = TaskDependencyAnalyzer.ParseDependencies(
            "Here are the dependencies:\n[{\"dependsOnTaskId\":\"01HZX\",\"reason\":\"shared contract\"}]",
            Logger);

        deps.Should().ContainSingle();
    }

    [Fact]
    public void Parses_an_empty_array_as_no_dependencies()
    {
        var deps = TaskDependencyAnalyzer.ParseDependencies("```json\n[]\n```", Logger);

        deps.Should().BeEmpty();
    }

    [Fact]
    public void Returns_null_for_prose_with_no_array()
    {
        TaskDependencyAnalyzer.ParseDependencies("No dependencies were detected.", Logger)
            .Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_a_truncated_array()
    {
        TaskDependencyAnalyzer.ParseDependencies(
            """[{"dependsOnTaskId":"01HZX","reason":"cut off""", Logger)
            .Should().BeNull();
    }
}
