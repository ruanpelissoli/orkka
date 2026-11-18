namespace Orkka.Modules.Projects.Domain;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Domain;
using Orkka.Modules.SourceControl.Contracts;

public sealed class ProjectRepo : TenantEntity
{
    public Ulid ProjectId { get; set; }
    public required string Name { get; set; }
    public required string GitUrl { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public WorkspaceStatus WorkspaceStatus { get; set; }
    public string CardColor { get; set; } = default!;

    /// <summary>A remote cloned with the developer's own git access, or a folder on their
    /// machine. Read from <see cref="GitUrl"/> every time rather than stored, so the two can
    /// never disagree.</summary>
    public RepoLocation Location => RepoUrl.Classify(GitUrl);

    /// <summary>The forge serving a remote — which CLI opens its pull requests (gh, az repos,
    /// glab). Detected from the URL's host when the repo is added; editable, because an ssh
    /// host alias (<c>git@github-work:org/app.git</c>) hides the forge. Meaningless for a
    /// folder.</summary>
    public ForgeKind ForgeKind { get; set; } = ForgeKind.Other;

    /// <summary>The account the forge CLI acts as for this repo, when the developer has more
    /// than one on the same host (<c>gh auth switch</c> accounts, an Azure DevOps organization).
    /// Null uses the CLI's active account.</summary>
    public string? CliAccount { get; set; }

    /// <summary>
    /// Toolchain this repo needs — one of <see cref="RepoStacks"/>, named after the worker
    /// image variants CD builds. Cloud tasks route to the worker pool whose image carries it.
    /// Repos are classified from their manifests (or their orkka.json `stack`) the first time
    /// a cloud worker has the worktree on disk, and stay null (= general pool) until then.
    /// </summary>
    public string? Stack { get; set; }

    /// <summary>
    /// Shell command that prepares a fresh task worktree so the app can start — installing
    /// dependencies, putting env in place. Per repo rather than per project because worktrees
    /// are per repo: a Next.js frontend and a .NET backend in one project need different
    /// commands.
    ///
    /// Never a place for secrets. On the user's own runner the command can copy their own
    /// (`cp ../../.env .`) and nothing leaves their disk; on Orkka's compute it runs through
    /// the agent Bash blocklist.
    /// </summary>
    public string? SetupCommand { get; set; }

    /// <summary>
    /// Command the QA agent runs in the task worktree to start this repo's app under test
    /// (e.g. "npm run dev"). Per repo for the same reason as <see cref="SetupCommand"/>:
    /// a project with a frontend and a backend has two apps to start, and the project-level
    /// setting it supersedes can only ever describe one of them.
    ///
    /// Null falls back to <c>ProjectSettings.QaRunCommand</c>, so single-repo projects
    /// configured before this existed keep working untouched.
    /// </summary>
    public string? QaRunCommand { get; set; }

    /// <summary>
    /// Command that proves this repo's code still compiles (e.g. "npm run build",
    /// "dotnet build"). Run after an automatic merge resolution, before the result is pushed:
    /// a resolver can produce marker-free, plausible-looking code that does not build, and the
    /// compiler's output is the one piece of feedback that turns the retry loop into a real
    /// fix-until-it-compiles cycle instead of a guess repeated three times.
    ///
    /// Per repo for the same reason as <see cref="SetupCommand"/>. Null means the check is
    /// skipped — the merge is still verified structurally (nothing unmerged, no committed
    /// conflict markers), just not compiled.
    ///
    /// Expected to be fast and side-effect free. It is not a test suite: this runs on the merge
    /// path, and anything slow here is paid on every conflicted merge.
    /// </summary>
    public string? BuildCommand { get; set; }

    /// <summary>
    /// Command that proves this repo's change is good — lint + typecheck + tests (e.g.
    /// "npm run lint &amp;&amp; npx tsc --noEmit &amp;&amp; npm test", "dotnet build &amp;&amp; dotnet test").
    /// Runs in the task worktree after every Developing run, before the PR/review: a dev agent
    /// can declare itself done with code that fails lint or tests, and the check's output is the
    /// feedback that sends the task back to the dev agent with something concrete to fix
    /// (bounded cycles, like QA). "Could not run" — no command, an old runner, a timeout — never
    /// blocks; only a non-zero exit does.
    ///
    /// Per repo for the same reason as <see cref="SetupCommand"/>. Null means no gate: the task
    /// flows straight to the PR/review as before.
    ///
    /// Never a place for secrets — it is stored in plain text and echoed into agent prompts.
    /// </summary>
    public string? CheckCommand { get; set; }

    /// <summary>Where this repository sits inside the project's base folder: relative, forward
    /// slashes, never <c>..</c>; <c>""</c> when the base folder IS the repository (a monorepo).
    /// Null for a repository that was not discovered from a base folder.</summary>
    public string? FolderPath { get; set; }

    /// <summary>A routing hint — what lives in this repository — shown to agents next to its
    /// siblings. Seeded from its <c>orkka.json</c> description or README, editable.</summary>
    public string? Description { get; set; }

    /// <summary>When a rescan of the base folder last failed to find this repository. Cleared
    /// when it reappears; nothing is deleted.</summary>
    public DateTimeOffset? MissingSince { get; set; }

    // Navigation
    public Project Project { get; set; } = null!;
}
