namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// One comparison shared by three callers that must agree: the runner deciding whether to
/// self-update on start, the local host's status endpoint, and the cloud fleet views. When
/// these drifted apart, a runner could self-update and still be shown as "update available".
/// </summary>
public sealed class RunnerVersionsTests
{
    [Theory]
    [InlineData("0.3.20", "0.3.21", true)]
    [InlineData("0.3.9", "0.3.20", true)]   // numeric, not lexicographic
    [InlineData("0.3.21", "0.3.21", false)]
    [InlineData("0.4.0", "0.3.21", false)]  // never "update" downwards
    [InlineData("0.3.20+abc123", "0.3.21", true)]  // source-link build metadata
    [InlineData("0.3.21", "0.3.21+abc123", false)]
    public void Newer_release_is_detected(string current, string latest, bool expected)
    {
        RunnerVersions.IsNewer(current, latest).Should().Be(expected);
    }

    /// <summary>A version neither side can read is not evidence of being behind. Treating
    /// it as "behind" would nag every user forever and, on the runner, let a malformed
    /// release overwrite a working install.</summary>
    [Theory]
    [InlineData(null, "0.3.21")]
    [InlineData("0.3.20", null)]
    [InlineData("", "0.3.21")]
    [InlineData("   ", "0.3.21")]
    [InlineData("dev", "0.3.21")]
    [InlineData("0.3.20", "nightly")]
    public void Unreadable_versions_never_claim_an_update(string? current, string? latest)
    {
        RunnerVersions.IsNewer(current, latest).Should().BeFalse();
    }

    [Theory]
    [InlineData("local-v0.3.21", "0.3.21")]
    [InlineData("0.3.21", "0.3.21")]        // already stripped
    [InlineData("v0.3.21", "v0.3.21")]      // not our prefix — left alone
    public void Tag_prefix_is_stripped(string tag, string expected)
    {
        RunnerVersions.FromTag(tag).Should().Be(expected);
    }
}
