namespace Orkka.Tests.Features.Tasks;

using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;

public sealed class UnifiedDiffStatsTests
{
    [Fact]
    public void Parse_EmptyDiff_ReturnsNothing()
    {
        var (add, del, files) = UnifiedDiffStats.Parse("");

        add.Should().Be(0);
        del.Should().Be(0);
        files.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ModifiedFile_CountsLinesExcludingHeaders()
    {
        var diff = """
            diff --git a/src/app.cs b/src/app.cs
            index 1234567..89abcde 100644
            --- a/src/app.cs
            +++ b/src/app.cs
            @@ -1,3 +1,4 @@
             using System;
            -var x = 1;
            +var x = 2;
            +var y = 3;
            """;

        var (add, del, files) = UnifiedDiffStats.Parse(diff);

        files.Should().ContainSingle();
        files[0].Path.Should().Be("src/app.cs");
        files[0].Status.Should().Be("modified");
        add.Should().Be(2);
        del.Should().Be(1);
    }

    [Fact]
    public void Parse_AddedAndDeletedFiles_DetectsStatus()
    {
        var diff = """
            diff --git a/new.txt b/new.txt
            new file mode 100644
            index 0000000..e69de29
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,1 @@
            +hello
            diff --git a/old.txt b/old.txt
            deleted file mode 100644
            index e69de29..0000000
            --- a/old.txt
            +++ /dev/null
            @@ -1,1 +0,0 @@
            -goodbye
            """;

        var (add, del, files) = UnifiedDiffStats.Parse(diff);

        files.Should().HaveCount(2);
        files[0].Status.Should().Be("added");
        files[0].Path.Should().Be("new.txt");
        files[1].Status.Should().Be("removed");
        files[1].Path.Should().Be("old.txt");
        add.Should().Be(1);
        del.Should().Be(1);
    }

    [Fact]
    public void Parse_RenamedFile_DetectsStatus()
    {
        var diff = """
            diff --git a/before.cs b/after.cs
            similarity index 95%
            rename from before.cs
            rename to after.cs
            """;

        var (add, del, files) = UnifiedDiffStats.Parse(diff);

        files.Should().ContainSingle();
        files[0].Path.Should().Be("after.cs");
        files[0].Status.Should().Be("renamed");
        add.Should().Be(0);
        del.Should().Be(0);
    }

    [Fact]
    public void Parse_SectionWithoutBHeader_IsSkipped()
    {
        var (_, _, files) = UnifiedDiffStats.Parse("diff --git garbage-header\n+++ nothing\n");

        files.Should().BeEmpty();
    }

    [Fact]
    public void ParseWithPatches_ExtractsPatchFromFirstHunk()
    {
        var diff = """
            diff --git a/src/app.cs b/src/app.cs
            index 1234567..89abcde 100644
            --- a/src/app.cs
            +++ b/src/app.cs
            @@ -1,3 +1,4 @@
             using System;
            -var x = 1;
            +var x = 2;
            +var y = 3;
            """;

        var (add, del, files) = UnifiedDiffStats.ParseWithPatches(diff);

        files.Should().ContainSingle();
        files[0].Patch.Should().StartWith("@@ -1,3 +1,4 @@");
        files[0].Patch.Should().Contain("+var y = 3;");
        files[0].Patch.Should().NotContain("index 1234567");
        files[0].PreviousPath.Should().BeNull();
        add.Should().Be(2);
        del.Should().Be(1);
    }

    [Fact]
    public void ParseWithPatches_RenamedFile_CarriesPreviousPath()
    {
        var diff = """
            diff --git a/before.cs b/after.cs
            similarity index 95%
            rename from before.cs
            rename to after.cs
            """;

        var (_, _, files) = UnifiedDiffStats.ParseWithPatches(diff);

        files.Should().ContainSingle();
        files[0].Path.Should().Be("after.cs");
        files[0].Status.Should().Be("renamed");
        files[0].PreviousPath.Should().Be("before.cs");
        files[0].Patch.Should().BeNull();
    }

    [Fact]
    public void ParseWithPatches_MultipleFiles_PatchStopsAtNextSection()
    {
        var diff = """
            diff --git a/one.txt b/one.txt
            --- a/one.txt
            +++ b/one.txt
            @@ -1,1 +1,1 @@
            -first old
            +first new
            diff --git a/two.txt b/two.txt
            --- a/two.txt
            +++ b/two.txt
            @@ -1,1 +1,1 @@
            -second old
            +second new
            """;

        var (_, _, files) = UnifiedDiffStats.ParseWithPatches(diff);

        files.Should().HaveCount(2);
        files[0].Patch.Should().Contain("+first new");
        files[0].Patch.Should().NotContain("second");
        files[1].Patch.Should().Contain("+second new");
        files[1].Patch.Should().NotContain("first");
    }
}
