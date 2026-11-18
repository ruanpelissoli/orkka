namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.SharedKernel.Ai;

public sealed class DefaultPromptsTests
{
    [Theory]
    [InlineData(AgentRole.PM, DefaultPrompts.PM)]
    [InlineData(AgentRole.Designer, DefaultPrompts.Designer)]
    [InlineData(AgentRole.FrontendDev, DefaultPrompts.FrontendDev)]
    [InlineData(AgentRole.BackendDev, DefaultPrompts.BackendDev)]
    [InlineData(AgentRole.FullStackDev, DefaultPrompts.FullStackDev)]
    [InlineData(AgentRole.QA, DefaultPrompts.QA)]
    [InlineData(AgentRole.CodeReviewer, DefaultPrompts.CodeReviewer)]
    [InlineData(AgentRole.DevOps, DefaultPrompts.DevOps)]
    [InlineData(AgentRole.SolutionArchitect, DefaultPrompts.SolutionArchitect)]
    [InlineData(AgentRole.GameDev, DefaultPrompts.GameDev)]
    [InlineData(AgentRole.GameDesigner, DefaultPrompts.GameDesigner)]
    [InlineData(AgentRole.DBA, DefaultPrompts.DBA)]
    [InlineData(AgentRole.Legal, DefaultPrompts.Legal)]
    [InlineData(AgentRole.MobileDev, DefaultPrompts.MobileDev)]
    public void ForRole_Unmanaged_MapsRoleToItsPersona(AgentRole role, string expected)
        => DefaultPrompts.ForRole(role).Should().Be(expected);

    [Fact]
    public void ForRole_CoversEveryRole_WithNonEmptyPersona()
    {
        foreach (var role in Enum.GetValues<AgentRole>())
            DefaultPrompts.ForRole(role).Should().NotBeNullOrWhiteSpace(
                $"role {role} must have a default persona");
    }

    // The personas are stored verbatim as agent SystemPrompts, which are capped at 12k
    // by CreateAgentValidator — a default must never exceed what a user could save.
    [Fact]
    public void ForRole_PersonasFitTheSystemPromptCap()
    {
        foreach (var role in Enum.GetValues<AgentRole>())
            DefaultPrompts.ForRole(role).Length.Should().BeLessThanOrEqualTo(12_000);
    }

    // Writing tests is the code-producing agent's job, and the QA lane's API mode reports
    // uncovered endpoints as a failure — so the agents that build endpoints must be told to
    // cover them, not merely to "run tests" as the personas used to say.
    [Theory]
    [InlineData(AgentRole.FrontendDev)]
    [InlineData(AgentRole.BackendDev)]
    [InlineData(AgentRole.FullStackDev)]
    public void DevPersonas_RequireUnitAndIntegrationTests(
        AgentRole role)
    {
        var persona = DefaultPrompts.ForRole(role);

        persona.Should().Contain("YOUR TESTS");
        persona.Should().Contain("Unit-test");
        persona.Should().Contain("Integration-test");
        // A repo with no framework is a thing to report, never to scaffold mid-task.
        persona.Should().Contain("scaffold");
    }

    // Agents narrate their work in comments by habit, which costs output tokens on every run
    // and leaves the noise in the customer's repo. Newly seeded code-writing agents carry the
    // rule in their persona; TaskOrchestrator injects it for the ones that already exist.
    [Theory]
    [InlineData(AgentRole.FrontendDev)]
    [InlineData(AgentRole.BackendDev)]
    [InlineData(AgentRole.FullStackDev)]
    [InlineData(AgentRole.GameDev)]
    [InlineData(AgentRole.MobileDev)]
    [InlineData(AgentRole.DevOps)]
    public void CodeWritingPersonas_CarryTheCommentPolicy(AgentRole role)
        => DefaultPrompts.ForRole(role)
            .Should().Contain(SharedPromptBlocks.CodeCommentPolicyMarker);

    // Everyone else either judges code or produces prose — the rule would only cost tokens.
    [Theory]
    [InlineData(AgentRole.PM)]
    [InlineData(AgentRole.Designer)]
    [InlineData(AgentRole.QA)]
    [InlineData(AgentRole.CodeReviewer)]
    [InlineData(AgentRole.SolutionArchitect)]
    [InlineData(AgentRole.GameDesigner)]
    [InlineData(AgentRole.DBA)]
    [InlineData(AgentRole.Legal)]
    public void NonCodeWritingPersonas_DoNotCarryTheCommentPolicy(AgentRole role)
        => DefaultPrompts.ForRole(role)
            .Should().NotContain(SharedPromptBlocks.CodeCommentPolicyMarker);

    // IsCodeWritingRole is what the runtime injection dispatches on — it must agree with the
    // set of personas above, or an agent gets the rule twice or not at all.
    [Fact]
    public void IsCodeWritingRole_MatchesThePersonasThatCarryTheRule()
    {
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            DefaultPrompts.ForRole(role)
                .Contains(SharedPromptBlocks.CodeCommentPolicyMarker, StringComparison.Ordinal)
                .Should().Be(role.IsCodeWritingRole(),
                    $"persona for {role} must agree with IsCodeWritingRole");
        }
    }

    // The block rides in every code-writing prompt, so a verbose rule defeats its own purpose.
    // Measured over normalised endings: the budget is about how much prompt the block spends,
    // which must not depend on whether the checkout materialised the source file CRLF or LF.
    [Fact]
    public void CodeCommentPolicy_StaysCompact()
    {
        SharedPromptBlocks.CodeCommentPolicy.Replace("\r\n", "\n")
            .Length.Should().BeLessThanOrEqualTo(600);
        SharedPromptBlocks.CodeCommentPolicy
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length.Should().BeLessThanOrEqualTo(6);
    }

    // Both narrow exceptions must survive any future edit: a blanket ban would have agents
    // stripping XML/JSDoc docs out of repos whose conventions (or lint rules) require them.
    [Fact]
    public void CodeCommentPolicy_KeepsItsTwoExceptions()
    {
        SharedPromptBlocks.CodeCommentPolicy.Should().Contain("doc comment");
        SharedPromptBlocks.CodeCommentPolicy.Should().Contain("lint rule");
        SharedPromptBlocks.CodeCommentPolicy.Should().Contain("WHY");
        // Team instructions are appended after this block and must be able to overrule it.
        SharedPromptBlocks.CodeCommentPolicy.Should().Contain("override this rule");
    }

    // The whole general quality instruction used to be "write clean, production-quality code".
    // Every persona whose output is code the customer keeps carries the engineering bar;
    // TaskOrchestrator injects it for the agents that already exist.
    [Theory]
    [InlineData(AgentRole.FrontendDev)]
    [InlineData(AgentRole.BackendDev)]
    [InlineData(AgentRole.FullStackDev)]
    [InlineData(AgentRole.GameDev)]
    [InlineData(AgentRole.MobileDev)]
    [InlineData(AgentRole.DevOps)]
    public void CodeWritingPersonas_CarryTheEngineeringBar(AgentRole role)
        => DefaultPrompts.ForRole(role)
            .Should().Contain(SharedPromptBlocks.EngineeringBarMarker);

    // IsCodeWritingRole is what the runtime injection dispatches on — it must agree with the
    // set of personas above, or an agent gets the bar twice or not at all.
    [Fact]
    public void IsCodeWritingRole_MatchesThePersonasThatCarryTheEngineeringBar()
    {
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            DefaultPrompts.ForRole(role)
                .Contains(SharedPromptBlocks.EngineeringBarMarker, StringComparison.Ordinal)
                .Should().Be(role.IsCodeWritingRole(),
                    $"persona for {role} must agree with IsCodeWritingRole");
        }
    }

    // The bar rides in every code-writing prompt against a 32k command-line cap, so the detail
    // belongs in the code-craft / frontend-craft / testing-craft skills (read on demand), not here.
    [Fact]
    public void EngineeringBar_StaysCompact()
        => SharedPromptBlocks.EngineeringBar.Replace("\r\n", "\n").Length.Should().BeLessThanOrEqualTo(1_600);

    // A default, not a boundary: placed before the team's own instructions so a team that wants
    // something else can say so and win. These are the load-bearing rules — rewording is fine,
    // dropping one silently is not.
    [Fact]
    public void EngineeringBar_KeepsItsNonNegotiables()
    {
        SharedPromptBlocks.EngineeringBar.Should().Contain("reuse");
        SharedPromptBlocks.EngineeringBar.Should().Contain("business rules");
        SharedPromptBlocks.EngineeringBar.Should().Contain("swallowed");
        SharedPromptBlocks.EngineeringBar.Should().Contain("N+1");
        SharedPromptBlocks.EngineeringBar.Should().Contain("git diff");
        SharedPromptBlocks.EngineeringBar.Should().Contain("code-craft");
        SharedPromptBlocks.EngineeringBar.Should().Contain("override");
    }

    // The reviewer must judge design by the same bar the dev agent builds to, or the pipeline
    // fights itself — and the objective list that bounces a task lives in the skill it names.
    [Fact]
    public void CodeReviewerPersona_ReviewsDesignAgainstTheEngineeringReviewSkill()
    {
        DefaultPrompts.CodeReviewer.Should().Contain("engineering-review");
        DefaultPrompts.CodeReviewer.Should().Contain("duplicate");
        DefaultPrompts.CodeReviewer.Should().Contain("swallowed");
    }

    // Nothing in a run used to ask for good UI, so agents shipped the median of their training
    // data. The personas that render screens carry the bar; the rest must not pay for it.
    [Theory]
    [InlineData(AgentRole.FrontendDev)]
    [InlineData(AgentRole.FullStackDev)]
    public void UiBuildingPersonas_CarryTheVisualBar(AgentRole role)
        => DefaultPrompts.ForRole(role)
            .Should().Contain(SharedPromptBlocks.VisualBarMarker);

    // IsUiBuildingRole is what the runtime injection dispatches on — it must agree with the
    // set of personas above, or an agent gets the bar twice or not at all.
    [Fact]
    public void IsUiBuildingRole_MatchesThePersonasThatCarryTheBar()
    {
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            DefaultPrompts.ForRole(role)
                .Contains(SharedPromptBlocks.VisualBarMarker, StringComparison.Ordinal)
                .Should().Be(role.IsUiBuildingRole(),
                    $"persona for {role} must agree with IsUiBuildingRole");
        }
    }

    // The block rides in every UI prompt against a 32k command-line cap, so the detail belongs
    // in the ui-craft skill (read on demand), not here.
    [Fact]
    public void VisualBar_StaysCompact()
        => SharedPromptBlocks.VisualBar.Replace("\r\n", "\n").Length.Should().BeLessThanOrEqualTo(1_400);

    // The bar is a default, not a boundary: it is placed before the team's own instructions so
    // a team that wants something else can say so and win. These are the load-bearing rules —
    // rewording is fine, dropping one silently is not.
    [Fact]
    public void VisualBar_KeepsItsNonNegotiables()
    {
        SharedPromptBlocks.VisualBar.Should().Contain("brand token");
        SharedPromptBlocks.VisualBar.Should().Contain("empty state");
        SharedPromptBlocks.VisualBar.Should().Contain("focus ring");
    }

    // Designer personas seeded before UX ownership shipped carry the literal opposite rule, so
    // the runtime block has to overrule it explicitly rather than merely adding to it.
    [Fact]
    public void DesignerUxOwnership_SupersedesTheOldProhibition()
    {
        DefaultPrompts.DesignerUxOwnership.Should().Contain("supersedes");
        DefaultPrompts.DesignerUxOwnership.Should().Contain("ux-direction");
        // The one boundary that survives the expansion: Muse still never commits code.
        DefaultPrompts.DesignerUxOwnership.Should().Contain("do not write application code");
    }

    // The seeded persona must agree with the injected block, or a fresh Designer reads one
    // rule on the Team page and a contradicting one at run time.
    [Fact]
    public void DesignerPersona_NoLongerForbidsUxDecisions()
        => DefaultPrompts.Designer.Should().NotContain("Make UX/layout decisions");

    // Otherwise the pipeline fights itself: the reviewer asks for the comments the dev agent
    // was told not to write, and the task bounces.
    [Fact]
    public void CodeReviewerPersona_DoesNotAskForCommentsForTheirOwnSake()
    {
        DefaultPrompts.CodeReviewer.Should().Contain("Flag code that is unclear");
        DefaultPrompts.CodeReviewer.Should().Contain("request comments for their own sake");
    }
}
