namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;

public sealed class SkillSeederTests
{
    private static readonly Ulid Org = Ulid.NewUlid();
    private static readonly Ulid Project = Ulid.NewUlid();

    // BuiltInKey is what the backfill dedupes on and what the filtered unique index enforces.
    // A duplicate key would make CreateMissing re-add a skill forever and blow up on save.
    [Fact]
    public void BuiltInKeys_AreUnique()
        => SkillSeeder.BuiltIns.Select(s => s.Key).Should().OnlyHaveUniqueItems();

    // Slug is unique per project (it is the /command and the SKILL.md folder name).
    [Fact]
    public void BuiltInSlugs_AreUniqueAndCommandSafe()
    {
        SkillSeeder.BuiltIns.Select(s => s.Slug).Should().OnlyHaveUniqueItems();

        foreach (var spec in SkillSeeder.BuiltIns)
            spec.Slug.Should().MatchRegex("^[a-z0-9-]{1,64}$",
                $"'{spec.Key}' is used as a URL segment and a folder name");
    }

    // A skill is a database column written at seed time, so an improved playbook used to reach
    // only projects created afterwards. Unedited built-ins now follow the spec; a row a human
    // touched is theirs.
    [Fact]
    public void RefreshUnedited_UpdatesStaleBuiltIns_AndLeavesHumanEditsAlone()
    {
        var spec = SkillSeeder.BuiltIns.Single(s => s.Key == "ui-craft");
        var stale = new AgentSkill
        {
            Id = Ulid.NewUlid(), OrgId = Org, ProjectId = Project, Slug = spec.Slug, Name = spec.Name,
            Description = "old description", Content = "# old text", IsBuiltIn = true, BuiltInKey = spec.Key,
        };
        var edited = new AgentSkill
        {
            Id = Ulid.NewUlid(), OrgId = Org, ProjectId = Project, Slug = "ui-craft-mine", Name = "Mine",
            Description = "mine", Content = "# mine", IsBuiltIn = true, BuiltInKey = spec.Key,
            UpdatedByUserId = "user_1",
        };
        var current = new AgentSkill
        {
            Id = Ulid.NewUlid(), OrgId = Org, ProjectId = Project, Slug = spec.Slug, Name = spec.Name,
            Description = spec.Description, Content = spec.Content, IsBuiltIn = true, BuiltInKey = spec.Key,
        };
        var custom = new AgentSkill
        {
            Id = Ulid.NewUlid(), OrgId = Org, ProjectId = Project, Slug = "deploy", Name = "Deploy",
            Content = "# deploy",
        };

        var changed = SkillSeeder.RefreshUnedited([stale, edited, current, custom]);

        changed.Should().Be(1);
        stale.Content.Should().Be(spec.Content);
        stale.Description.Should().Be(spec.Description);
        edited.Content.Should().Be("# mine");
        custom.Content.Should().Be("# deploy");
    }

    // Prod, CookieLand (2026-09-08): a `--spacing-md` token wrapped the homepage one word per
    // line, the shell had no logo, the landing screen was an empty state, and the layout only
    // held at one width. The UI playbook now names each of those.
    [Fact]
    public void UiCraft_CarriesTheLessonsFromTheFirstManagedApp()
    {
        var content = SkillSeeder.BuiltIns.Single(s => s.Key == "ui-craft").Content;

        content.Should().Contain("Responsive by default");
        content.Should().Contain("Never add `--spacing-*` keys");
        content.Should().Contain("max-w-md");
        content.Should().Contain("public/brand/");
        content.Should().Contain("picsum");
        content.Should().Contain("one-word-per-line");
    }

    [Fact]
    public void FrontendCraft_NamesTheTailwindNamespaceTrap()
        => SkillSeeder.BuiltIns.Single(s => s.Key == "frontend-craft").Content
            .Should().Contain("--spacing-*").And.Contain("Responsive by default");

    // The description rides in the system prompt on EVERY run of every agent holding the
    // skill, against a 32k command-line cap — and TaskOrchestrator truncates past 300 chars,
    // which would cut a built-in mid-sentence.
    [Fact]
    public void BuiltInDescriptions_FitTheirPromptBudget()
    {
        foreach (var spec in SkillSeeder.BuiltIns)
        {
            spec.Description.Should().NotBeNullOrWhiteSpace();
            spec.Description.Length.Should().BeLessThanOrEqualTo(300, $"'{spec.Key}' is truncated past this");
        }
    }

    [Fact]
    public void BuiltInsHaveContentAndAtLeastOneDefaultRole()
    {
        foreach (var spec in SkillSeeder.BuiltIns)
        {
            spec.Content.Should().NotBeNullOrWhiteSpace($"'{spec.Key}' has nothing to read");
            spec.DefaultRoles.Should().NotBeEmpty($"'{spec.Key}' would be seeded but assigned to nobody");
        }
    }

    // The UI-quality skills are the whole point of the set: an agent that renders screens must
    // get them without anyone assigning them by hand.
    [Theory]
    [InlineData("ui-craft", AgentRole.FrontendDev)]
    [InlineData("ui-craft", AgentRole.FullStackDev)]
    [InlineData("ui-craft", AgentRole.MobileDev)]
    [InlineData("brand-consistency", AgentRole.Designer)]
    [InlineData("brand-consistency", AgentRole.FrontendDev)]
    [InlineData("brand-consistency", AgentRole.FullStackDev)]
    [InlineData("ux-direction", AgentRole.Designer)]
    [InlineData("design-review", AgentRole.CodeReviewer)]
    public void UiSkills_ReachTheRolesThatNeedThem(string key, AgentRole role)
        => SkillSeeder.BuiltIns.Single(s => s.Key == key)
            .DefaultRoles.Should().Contain(role);

    // The engineering-quality set: the agent reads these from disk on demand, so Content may be
    // long — but the SKILL.md injection has a hard ceiling, and the description still rides in
    // every prompt.
    [Theory]
    [InlineData("code-craft")]
    [InlineData("frontend-craft")]
    [InlineData("testing-craft")]
    [InlineData("engineering-review")]
    public void EngineeringSkills_ExistAndFitTheirBudgets(string key)
    {
        var spec = SkillSeeder.BuiltIns.Should().ContainSingle(s => s.Key == key).Subject;

        spec.Slug.Should().Be(key);
        spec.Content.Should().NotBeNullOrWhiteSpace();
        spec.Content.Length.Should().BeLessThanOrEqualTo(32_000, $"'{key}' is injected as a SKILL.md");
        spec.Description.Length.Should().BeLessThanOrEqualTo(300, $"'{key}' is truncated past this");
    }

    // code-craft is the writing bar for everyone whose output is code the customer keeps — the
    // same audience as the code-comment policy. If IsCodeWritingRole grows a role, the skill
    // must follow, and a reviewer or PM must never be handed a rule about writing code.
    [Fact]
    public void AssignDefaults_AttachesCodeCraftToExactlyTheCodeWritingRoles()
    {
        var seeded = SkillSeeder.CreateMissing(Org, Project, []);
        var codeCraft = seeded.Single(s => s.BuiltInKey == "code-craft").Id.ToGuid();
        var agents = Enum.GetValues<AgentRole>().Select(NewAgent).ToList();

        SkillSeeder.AssignDefaults(seeded, agents);

        foreach (var agent in agents)
        {
            if (agent.Role.IsCodeWritingRole())
                agent.SkillIds.Should().Contain(codeCraft, $"{agent.Role} writes code the customer keeps");
            else
                agent.SkillIds.Should().NotContain(codeCraft, $"{agent.Role} does not write code");
        }
    }

    [Fact]
    public void AssignDefaults_AttachesEngineeringReviewOnlyToTheCodeReviewer()
    {
        var seeded = SkillSeeder.CreateMissing(Org, Project, []);
        var review = seeded.Single(s => s.BuiltInKey == "engineering-review").Id.ToGuid();
        var agents = Enum.GetValues<AgentRole>().Select(NewAgent).ToList();

        SkillSeeder.AssignDefaults(seeded, agents);

        agents.Where(a => a.SkillIds.Contains(review)).Select(a => a.Role)
            .Should().BeEquivalentTo([AgentRole.CodeReviewer]);
    }

    // frontend-craft is React/Next-specific: MobileDev and GameDev get ui-craft (platform-neutral)
    // but not a skill that would point them at server components.
    [Fact]
    public void AssignDefaults_AttachesFrontendCraftOnlyToWebFrontendRoles()
    {
        var seeded = SkillSeeder.CreateMissing(Org, Project, []);
        var frontend = seeded.Single(s => s.BuiltInKey == "frontend-craft").Id.ToGuid();
        var agents = Enum.GetValues<AgentRole>().Select(NewAgent).ToList();

        SkillSeeder.AssignDefaults(seeded, agents);

        agents.Where(a => a.SkillIds.Contains(frontend)).Select(a => a.Role)
            .Should().BeEquivalentTo([AgentRole.FrontendDev, AgentRole.FullStackDev]);
    }

    // The personas point agents at these two by name. A rename here without one there leaves
    // the agent hunting for a skill that does not exist.
    [Fact]
    public void SkillsNamedInPersonas_Exist()
    {
        var slugs = SkillSeeder.BuiltIns.Select(s => s.Slug).ToList();
        slugs.Should().Contain("ux-direction", "DefaultPrompts.Designer references it by slug");
        slugs.Should().Contain("design-review", "DefaultPrompts.CodeReviewer references it by slug");
    }

    [Fact]
    public void CreateMissing_SkipsWhatTheProjectAlreadyHas()
    {
        var have = new[] { "ui-craft", "groom-backlog" };

        var seeded = SkillSeeder.CreateMissing(Org, Project, have);

        seeded.Select(s => s.BuiltInKey).Should().NotContain(have);
        seeded.Should().HaveCount(SkillSeeder.BuiltIns.Count - have.Length);
        seeded.Should().OnlyContain(s => s.IsBuiltIn && s.IsEnabled);
    }

    // The backfill path: an existing project's agents must pick the new skills up without
    // anyone touching the Team page.
    [Fact]
    public void AssignDefaults_AttachesNewSkillsToTheirRoles()
    {
        var seeded = SkillSeeder.CreateMissing(Org, Project, []);
        var nova = NewAgent(AgentRole.FullStackDev);
        var atlas = NewAgent(AgentRole.BackendDev);

        SkillSeeder.AssignDefaults(seeded, [nova, atlas]);

        var uiCraft = seeded.Single(s => s.BuiltInKey == "ui-craft").Id.ToGuid();
        nova.SkillIds.Should().Contain(uiCraft);
        atlas.SkillIds.Should().NotContain(uiCraft, "a backend agent ships no UI");
    }

    // Re-running the backfill must not append a second copy of the same skill id.
    [Fact]
    public void AssignDefaults_IsIdempotent()
    {
        var seeded = SkillSeeder.CreateMissing(Org, Project, []);
        var nova = NewAgent(AgentRole.FullStackDev);

        SkillSeeder.AssignDefaults(seeded, [nova]);
        var afterFirst = nova.SkillIds.ToArray();
        var changed = SkillSeeder.AssignDefaults(seeded, [nova]);

        changed.Should().Be(0);
        nova.SkillIds.Should().BeEquivalentTo(afterFirst);
    }

    private static ProjectAgent NewAgent(AgentRole role) => new()
    {
        Id = Ulid.NewUlid(),
        OrgId = Org,
        ProjectId = Project,
        Name = role.ToString(),
        Role = role,
        Model = "claude-sonnet-5",
        SystemPrompt = "persona",
    };
}
