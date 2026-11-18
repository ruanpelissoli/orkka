namespace Orkka.Tests.Execution;

using FluentAssertions;
using Orkka.Modules.Tasks.Contracts;

/// <summary>A tracker task's branch: the ticket source's template, always a valid git ref.</summary>
public sealed class TrackerBranchNameTests
{
    [Theory]
    [InlineData(null, TaskType.Feature, "SHOP-12", "[SHOP-12] Add invoice export", "feature/SHOP-12-add-invoice-export")]
    [InlineData("{type}/{key}-{slug}", TaskType.BugFix, "SHOP-7", "[SHOP-7] Totals off by one cent", "bugfix/SHOP-7-totals-off-by-one-cent")]
    [InlineData("{key}", TaskType.Feature, "4567", "Whatever", "4567")]
    [InlineData("users/dana/{key}", TaskType.Refactor, "AB-1", "x", "users/dana/AB-1")]
    [InlineData("{type}/{key}-{slug}", TaskType.Improvement, "SHOP-3", "Ação: résumé — émail!!", "improvement/SHOP-3-acao-resume-email")]
    public void RendersTheTemplate(string? template, TaskType type, string key, string title, string expected)
        => TrackerBranchName.Render(template, type, key, title).Should().Be(expected);

    [Fact]
    public void LongTitles_AreCut()
    {
        var name = TrackerBranchName.Render(null, TaskType.Feature, "SHOP-12",
            "Add an export of every invoice the customer ever received, in CSV and XLSX, with totals");

        name.Should().StartWith("feature/SHOP-12-add-an-export");
        name!.Length.Should().BeLessThanOrEqualTo(120);
        name.Should().NotEndWith("-");
    }

    [Theory]
    [InlineData("../{key}..lock")]
    [InlineData("  {key} with spaces~^:?*[\\")]
    [InlineData("//{key}//.hidden/")]
    public void UnsafeTemplates_StillMakeAValidRef(string template)
    {
        var name = TrackerBranchName.Render(template, TaskType.Feature, "SHOP-12", "t");

        name.Should().NotBeNullOrEmpty();
        name.Should().NotContain("..").And.NotContain(" ").And.NotContain("~").And.NotContain(":")
            .And.NotStartWith("/").And.NotEndWith("/").And.NotEndWith(".lock").And.NotEndWith(".");
        name!.Split('/').Should().OnlyContain(part => part.Length > 0 && !part.StartsWith('.'));
    }
}
