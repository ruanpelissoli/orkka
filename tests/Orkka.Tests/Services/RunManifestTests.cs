namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure.Services;
using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// The runnable contract's plumbing: a repo's orkka.json becomes run targets without a model,
/// the bootstrap task lands first with everything depending on it, and "empty repo" means
/// what it says.
/// </summary>
public sealed class RunManifestTests
{
    private const string Repo = "01JQZX0000000000000000000A";

    // ── stack: the section that routes cloud tasks to a toolchain image ────────────────

    [Theory]
    [InlineData("""{ "stack": "go" }""", "go")]
    [InlineData("""{ "stack": "Ruby", "run": { "targets": [] } }""", "ruby")]
    [InlineData("""{ "stack": "cpp" }""", "cpp")]
    [InlineData("""/* a comment */ { "stack": "jvm", }""", "jvm")]
    public void OrkkaManifest_TryReadStack_ReadsAKnownStack(string json, string expected)
        => OrkkaManifest.TryReadStack(json).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{ "run": { "targets": [] } }""")]
    [InlineData("""{ "stack": "cobol" }""")]
    [InlineData("""{ "stack": 42 }""")]
    [InlineData("""[ "stack" ]""")]
    [InlineData("""{ not json""")]
    public void OrkkaManifest_TryReadStack_AbsentUnknownOrBroken_IsNull(string? json)
        // Null, never "general": the caller falls back to classifying the tree, so a typo in
        // the file cannot route a Go repo onto the Node-only pool.
        => OrkkaManifest.TryReadStack(json).Should().BeNull();

    [Fact]
    public void OrkkaManifest_TryReadStackFromRepo_ReadsTheFileAtTheRoot()
    {
        var root = Directory.CreateTempSubdirectory("orkka-manifest-").FullName;
        try
        {
            OrkkaManifest.TryReadStackFromRepo(root).Should().BeNull("no file yet");
            File.WriteAllText(Path.Combine(root, OrkkaManifest.FileName), """{ "stack": "rust" }""");
            OrkkaManifest.TryReadStackFromRepo(root).Should().Be("rust");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryParse_ReadsTargetsAndFillsRepoId()
    {
        const string json = """
            {
              "run": {
                "targets": [
                  { "key": "db", "label": "Postgres", "kind": "infra",
                    "command": "docker compose up postgres",
                    "stopCommand": "docker compose stop postgres", "port": 5432 },
                  { "key": "web", "label": "Web", "kind": "web", "command": "npm run dev",
                    "installCommand": "npm install", "port": 3000, "dependsOn": ["db"],
                    "restartOnMerge": false, "previewPath": "/" }
                ],
                "notes": "Copy .env.example to .env"
              }
            }
            """;

        var parsed = RunManifest.TryParse(json, Repo);

        parsed.Should().NotBeNull();
        parsed!.Targets.Should().HaveCount(2);
        parsed.Targets.Should().OnlyContain(t => t.RepoId == Repo);
        parsed.Targets[0].Kind.Should().Be("infra");
        parsed.Targets[0].StopCommand.Should().Be("docker compose stop postgres");
        parsed.Targets[1].DependsOn.Should().Equal("db");
        parsed.Targets[1].InstallCommand.Should().Be("npm install");
        parsed.Notes.Should().Be("Copy .env.example to .env");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{ \"run\": {} }")]
    [InlineData("{ \"run\": { \"targets\": [] } }")]
    [InlineData("{ \"run\": { \"targets\": [ { \"key\": \"web\" } ] } }")]
    public void TryParse_TreatsAnythingUnusableAsNoManifest(string? json)
    {
        RunManifest.TryParse(json, Repo).Should().BeNull();
    }

    [Fact]
    public void TryParse_ToleratesCommentsTrailingCommasAndUnknownFields()
    {
        const string json = """
            {
              // the run section
              "run": {
                "targets": [
                  { "key": "api", "kind": "api", "command": "go run .", "extra": true, },
                ],
              },
              "otherSection": { "ignored": 1 }
            }
            """;

        var parsed = RunManifest.TryParse(json, Repo);

        parsed.Should().NotBeNull();
        parsed!.Targets.Single().Command.Should().Be("go run .");
    }

    [Fact]
    public void Normalize_KeepsTheNewFieldsAndDropsUrlsForInfra()
    {
        var targets = RunConfigDetector.Normalize(
            [
                new RunTargetDto("db", "Postgres", "infra", Repo, "docker compose up postgres",
                    Port: 5432, StopCommand: " docker compose stop postgres ", PreviewPath: "/"),
                new RunTargetDto("web", "Web", "web", Repo, "npm run dev",
                    Port: 3000, DependsOn: ["db"], RestartOnMerge: true, PreviewPath: " /admin "),
            ],
            [Repo]);

        var db = targets.Single(t => t.Key == "db");
        db.Url.Should().BeNull("a browser cannot open Postgres");
        db.PreviewPath.Should().BeNull();
        db.StopCommand.Should().Be("docker compose stop postgres");

        var web = targets.Single(t => t.Key == "web");
        web.Url.Should().Be("http://localhost:3000");
        web.PreviewPath.Should().Be("/admin");
        web.RestartOnMerge.Should().BeTrue();
        web.DependsOn.Should().Equal("db");
    }

    [Fact]
    public void MergeManifests_ManifestRepoWinsOverTheModelForItsOwnRepo()
    {
        const string other = "01JQZX0000000000000000000B";
        var manifest = new ParsedRunManifest(
            [new RunTargetDto("web", "Web", "web", Repo, "pnpm dev", Port: 3000)], null);
        var evidence = new RunConfigDetector.Evidence(
            [
                new RunConfigDetector.RepoEvidence(Ulid.Parse(Repo), "web", "", "fp", manifest),
                new RunConfigDetector.RepoEvidence(Ulid.Parse(other), "api", "", "fp", null),
            ],
            "", "fp");
        RunTargetDto[] fromModel =
        [
            new("web", "Web (model)", "web", Repo, "npm run dev", Port: 3001),
            new("api", "API", "api", other, "go run .", Port: 8080),
        ];

        var merged = RunConfigDetector.MergeManifests(fromModel, evidence);

        merged.Should().HaveCount(2);
        merged.Single(t => t.Key == "web").Command.Should().Be("pnpm dev");
        merged.Single(t => t.Key == "api").RepoId.Should().Be(other);
        evidence.AllHaveManifests.Should().BeFalse();
    }

}
