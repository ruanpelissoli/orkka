namespace Orkka.Tests.Execution;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Tasks.Contracts;

public sealed class ResolverFailureKindTests
{
    /// <summary>A resolver run the runner cut off never tried; one that ran and failed did.</summary>
    [Theory]
    [InlineData("Your runner disconnected mid-run. Reconnect it and retry the task.", MergeFailureKind.EnvironmentBlocked)]
    [InlineData("This repository is a folder on your machine, and your runner is not connected.", MergeFailureKind.EnvironmentBlocked)]
    [InlineData("The agent hit its maximum-turns limit before it could finish the reply.", MergeFailureKind.Unknown)]
    public void AResolverRun_CutOffByTheEnvironment_IsRecordedAsSuch(string error, MergeFailureKind expected)
        => TaskOrchestrator.ResolverFailureKind(error).Should().Be(expected);
}
