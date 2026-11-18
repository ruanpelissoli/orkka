namespace Orkka.Tests.Features.CodeReview;

using FluentAssertions;
using Orkka.Modules.Tasks.Features.CodeReview;

/// <summary>
/// When the branch can no longer be diffed live, the review screen falls back to the newest
/// code-change snapshot — which holds paths and counts only, never code.
/// </summary>
public sealed class SnapshotFilesTests
{
    [Fact]
    public void FilesComeBackWithStatsAndNoPatch()
    {
        var files = GetPullRequestEndpoint.SnapshotFiles(
            """[{"path":"src/a.ts","status":"modified","additions":3,"deletions":1},{"path":"b.md","status":"added","additions":10,"deletions":0}]""");

        files.Should().HaveCount(2);
        files[0].Should().BeEquivalentTo(new { Filename = "src/a.ts", Status = "modified", Additions = 3, Deletions = 1, Patch = (string?)null });
        files.Should().OnlyContain(f => f.Patch == null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void NothingUsable_MeansNoFiles(string? json)
    {
        GetPullRequestEndpoint.SnapshotFiles(json).Should().BeEmpty();
    }
}
