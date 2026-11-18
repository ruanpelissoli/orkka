namespace Orkka.LocalRunner;

using System.Diagnostics;
using System.Text.Json;
using Orkka.LocalRunner.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Opens and reads pull requests on the developer's forge with the developer's own CLI —
/// <c>gh</c> (GitHub), <c>az repos</c> (Azure DevOps), <c>glab</c> (GitLab). The pull request is
/// theirs: authored by their account, on their employer's repository, exactly as if they had
/// typed the command. Orkka holds no forge credential and never calls a forge API itself.
///
/// Idempotent: an open pull request for the head branch is found and returned before anything
/// is created, so a retried publish never opens a second one.
/// </summary>
public sealed class ForgeCli(ILogger<ForgeCli> logger)
{
    /// <summary>The CLI that serves a forge, or null when none does (Bitbucket, unknown hosts).</summary>
    public static string? ExecutableFor(ForgeKind forge) => forge switch
    {
        ForgeKind.GitHub => "gh",
        ForgeKind.AzureDevOps => "az",
        ForgeKind.GitLab => "glab",
        _ => null,
    };

    // Signing a CLI in happens on human timescales, and `gh auth status` asks GitHub — so the
    // answer is re-probed every few minutes, not on every 60s Hello.
    private static readonly TimeSpan StatusTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private RunnerForgeCliStatus[]? _status;
    private DateTimeOffset _statusAt;

    /// <summary>
    /// gh, az and glab: installed, signed in, as whom. Cached for a few minutes; a probe that
    /// hangs (a CLI waiting on a network it cannot reach) gives up after a few seconds and
    /// reports the CLI as not ready rather than holding up the Hello.
    /// </summary>
    public async Task<RunnerForgeCliStatus[]> DetectAsync(CancellationToken ct)
    {
        if (_status is { } cached && DateTimeOffset.UtcNow - _statusAt < StatusTtl)
            return cached;

        await _statusGate.WaitAsync(ct);
        try
        {
            if (_status is { } fresh && DateTimeOffset.UtcNow - _statusAt < StatusTtl)
                return fresh;

            var probes = await Task.WhenAll(ProbeGitHubAsync(ct), ProbeAzureAsync(ct), ProbeGitLabAsync(ct));
            _status = probes;
            _statusAt = DateTimeOffset.UtcNow;
            return probes;
        }
        finally
        {
            _statusGate.Release();
        }
    }

    private static async Task<RunnerForgeCliStatus> ProbeGitHubAsync(CancellationToken ct)
    {
        var (found, exit, output) = await ProbeAsync("gh", ["auth", "status"], ct);
        if (!found)
            return new("gh", false, false, Detail: "Install the GitHub CLI (https://cli.github.com).");

        var accounts = SignedInAccounts(output);
        return exit == 0
            ? new("gh", true, true, accounts.Length > 0 ? accounts : null)
            : new("gh", true, false, accounts.Length > 0 ? accounts : null, "Run `gh auth login`.");
    }

    private static async Task<RunnerForgeCliStatus> ProbeAzureAsync(CancellationToken ct)
    {
        var (found, exit, output) = await ProbeAsync(
            "az", ["account", "show", "--query", "user.name", "--output", "tsv"], ct);
        if (!found)
            return new("az", false, false,
                Detail: "Install the Azure CLI and its azure-devops extension (`az extension add --name azure-devops`).");
        if (exit != 0)
            return new("az", true, false, Detail: "Run `az login`.");

        var account = output.Trim();
        var (_, extensionExit, _) = await ProbeAsync("az", ["extension", "show", "--name", "azure-devops"], ct);
        return new("az", true, true, account.Length > 0 ? [account] : null,
            extensionExit == 0 ? null : "Pull requests need the azure-devops extension: `az extension add --name azure-devops`.");
    }

    private static async Task<RunnerForgeCliStatus> ProbeGitLabAsync(CancellationToken ct)
    {
        var (found, exit, output) = await ProbeAsync("glab", ["auth", "status"], ct);
        if (!found)
            return new("glab", false, false, Detail: "Install the GitLab CLI (https://gitlab.com/gitlab-org/cli).");

        var accounts = SignedInAccounts(output);
        return exit == 0
            ? new("glab", true, true, accounts.Length > 0 ? accounts : null)
            : new("glab", true, false, Detail: "Run `glab auth login`.");
    }

    /// <summary>
    /// "account@host" for every signed-in line of a `gh auth status` / `glab auth status`
    /// report: gh says "Logged in to github.com account dana (keyring)" (older versions "as
    /// dana"), glab "Logged in to gitlab.com as dana (…)".
    /// </summary>
    internal static string[] SignedInAccounts(string output)
        => [.. System.Text.RegularExpressions.Regex
            .Matches(output, @"Logged in to (\S+) (?:account|as) (\S+)")
            .Select(m => $"{m.Groups[2].Value.TrimEnd('.', ',')}@{m.Groups[1].Value}")
            .Distinct()];

    /// <summary>Runs a status command: found (the executable started), exit code, and stdout
    /// and stderr together — gh and glab print their status to stderr in some versions.</summary>
    private static async Task<(bool Found, int ExitCode, string Output)> ProbeAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["NO_COLOR"] = "1";

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, -1, "");
        }
        if (process is null)
            return (false, -1, "");

        using var _ = process;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (true, -1, "");
        }

        return (true, process.ExitCode, (await stdout) + "\n" + (await stderr));
    }

    public async Task<RunnerOpResult> OpenAsync(
        string originUrl, ForgeKind forge, string? cliAccount,
        string headBranch, string baseBranch, string title, string body, CancellationToken ct, bool draft = false)
    {
        if (ExecutableFor(forge) is null)
            return RunnerOpResult.Fail(
                $"Orkka has no CLI for this repository's host — branch '{headBranch}' is pushed; open the pull request into '{baseBranch}' yourself.");

        var repo = RepoUrl.ParseRef(originUrl, forge);
        if (repo is null)
            return RunnerOpResult.Fail($"Could not read the repository from its remote '{originUrl}'.");

        return forge switch
        {
            ForgeKind.GitHub => await OpenGitHubAsync(repo, cliAccount, headBranch, baseBranch, title, body, draft, ct),
            ForgeKind.AzureDevOps => await OpenAzureDevOpsAsync(repo, headBranch, baseBranch, title, body, draft, ct),
            _ => await OpenGitLabAsync(repo, headBranch, baseBranch, title, body, draft, ct),
        };
    }

    /// <summary>A Story's pull request: draft → ready for review, or with <paramref name="undo"/>
    /// back to draft (a sub-task joined after it was readied).</summary>
    public async Task<RunnerOpResult> MarkReadyAsync(
        string originUrl, ForgeKind forge, string? cliAccount, string pullRequestUrl, bool undo, CancellationToken ct)
    {
        var repo = RepoUrl.ParseRef(originUrl, forge);
        if (repo is null || ExecutableFor(forge) is null)
            return RunnerOpResult.Fail("This repository's pull requests cannot be changed from here.");

        var (exe, args, env) = forge switch
        {
            ForgeKind.GitHub => ("gh", ForgeCliArgs.GitHubReady(pullRequestUrl, GitHubRepoArgument(repo), undo),
                await GitHubEnvironmentAsync(repo, cliAccount, ct)),
            ForgeKind.AzureDevOps => ("az", ForgeCliArgs.AzureReady(AzurePullRequestId(pullRequestUrl), AzureOrganizationUrl(repo), undo), null),
            _ => ("glab", ForgeCliArgs.GitLabReady(pullRequestUrl, repo.Slug, undo), null),
        };
        var result = await RunAsync(exe, args, env, ct);
        if (result.Success)
            logger.LogInformation("Marked {Url} {State}", pullRequestUrl, undo ? "draft" : "ready for review");
        return result.Success ? RunnerOpResult.Ok(pullRequestUrl) : RunnerOpResult.Fail(
            $"{exe} could not mark the pull request {(undo ? "as a draft" : "ready for review")}: {result.Error}");
    }

    /// <summary>Rewrites a pull request's description (a Story's checklist as its sub-tasks land).</summary>
    public async Task<RunnerOpResult> UpdateBodyAsync(
        string originUrl, ForgeKind forge, string? cliAccount, string pullRequestUrl, string body, CancellationToken ct)
    {
        var repo = RepoUrl.ParseRef(originUrl, forge);
        if (repo is null || ExecutableFor(forge) is null)
            return RunnerOpResult.Fail("This repository's pull requests cannot be changed from here.");

        switch (forge)
        {
            case ForgeKind.GitHub:
            {
                var bodyFile = Path.GetTempFileName();
                try
                {
                    await File.WriteAllTextAsync(bodyFile, body, ct);
                    var edit = await RunAsync("gh", ForgeCliArgs.GitHubEditBody(pullRequestUrl, GitHubRepoArgument(repo), bodyFile),
                        await GitHubEnvironmentAsync(repo, cliAccount, ct), ct);
                    return edit.Success ? RunnerOpResult.Ok(pullRequestUrl) : edit;
                }
                finally
                {
                    TryDelete(bodyFile);
                }
            }
            case ForgeKind.AzureDevOps:
            {
                var update = await RunAsync("az", ForgeCliArgs.AzureEditBody(
                    AzurePullRequestId(pullRequestUrl), AzureOrganizationUrl(repo), body), null, ct);
                return update.Success ? RunnerOpResult.Ok(pullRequestUrl) : update;
            }
            default:
            {
                var update = await RunAsync("glab", ForgeCliArgs.GitLabEditBody(pullRequestUrl, repo.Slug, body), null, ct);
                return update.Success ? RunnerOpResult.Ok(pullRequestUrl) : update;
            }
        }
    }

    private static string AzurePullRequestId(string pullRequestUrl) => pullRequestUrl.TrimEnd('/').Split('/')[^1];

    /// <summary>"open", "merged" or "closed" for a pull request this class opened, or a
    /// failure when the CLI cannot say.</summary>
    public async Task<RunnerOpResult> GetStateAsync(
        string originUrl, ForgeKind forge, string? cliAccount, string pullRequestUrl, CancellationToken ct)
    {
        var repo = RepoUrl.ParseRef(originUrl, forge);
        if (repo is null || ExecutableFor(forge) is null)
            return RunnerOpResult.Fail("This repository's pull requests cannot be read from here.");

        switch (forge)
        {
            case ForgeKind.GitHub:
            {
                var view = await RunAsync("gh", ["pr", "view", pullRequestUrl, "--json", "state", "--jq", ".state"],
                    await GitHubEnvironmentAsync(repo, cliAccount, ct), ct);
                return view.Success ? RunnerOpResult.Ok(NormalizeState(view.Value)) : view;
            }
            case ForgeKind.AzureDevOps:
            {
                var id = pullRequestUrl.TrimEnd('/').Split('/')[^1];
                var show = await RunAsync("az", ["repos", "pr", "show", "--id", id,
                    "--organization", AzureOrganizationUrl(repo), "--query", "status", "-o", "tsv"], null, ct);
                // Azure DevOps says "completed" for a merged pull request and "abandoned" for a closed one.
                return show.Success ? RunnerOpResult.Ok(NormalizeState(show.Value)) : show;
            }
            default:
            {
                var view = await RunAsync("glab", ["mr", "view", pullRequestUrl, "--repo", repo.Slug, "--output", "json"], null, ct);
                if (!view.Success)
                    return view;
                using var doc = JsonDocument.Parse(view.Value ?? "{}");
                return RunnerOpResult.Ok(NormalizeState(
                    doc.RootElement.TryGetProperty("state", out var state) ? state.GetString() : null));
            }
        }
    }

    internal static string NormalizeState(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "merged" or "completed" => "merged",
        "closed" or "abandoned" => "closed",
        "open" or "opened" or "active" => "open",
        _ => "unknown",
    };

    // ── GitHub ───────────────────────────────────────────────────────────────

    private async Task<RunnerOpResult> OpenGitHubAsync(
        ForgeRepoRef repo, string? cliAccount, string head, string baseBranch,
        string title, string body, bool draft, CancellationToken ct)
    {
        var env = await GitHubEnvironmentAsync(repo, cliAccount, ct);
        var repoArg = GitHubRepoArgument(repo);

        var auth = await RunAsync("gh", ["auth", "status", "--hostname", GitHubHost(repo)], env, ct);
        if (auth.Error == NotInstalled)
            return RunnerOpResult.Fail("The GitHub CLI (gh) is not installed on this machine — install it from https://cli.github.com and run `gh auth login`.");
        if (!auth.Success)
            return RunnerOpResult.Fail($"The GitHub CLI is not signed in to {GitHubHost(repo)} — run `gh auth login` on this machine.");

        var existing = await RunAsync("gh", ["pr", "list", "--repo", repoArg, "--head", head, "--state", "open",
            "--json", "url", "--jq", ".[0].url"], env, ct);
        if (existing.Success && FirstUrl(existing.Value) is { } openUrl)
            return RunnerOpResult.Ok(openUrl);

        var bodyFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(bodyFile, body, ct);
            var create = await RunAsync("gh", ForgeCliArgs.GitHubCreate(repoArg, head, baseBranch, title, bodyFile, draft), env, ct);
            return create.Success && FirstUrl(create.Value) is { } url
                ? Opened(url, repo)
                : RunnerOpResult.Fail($"gh could not open the pull request: {create.Error ?? create.Value}");
        }
        finally
        {
            TryDelete(bodyFile);
        }
    }

    /// <summary>A developer with several accounts on one host (a personal and a work one)
    /// names the account per repo; the call runs with that account's token, scoped to it.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> GitHubEnvironmentAsync(
        ForgeRepoRef repo, string? cliAccount, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cliAccount))
            return null;

        var token = await RunAsync("gh", ["auth", "token", "--hostname", GitHubHost(repo), "--user", cliAccount], null, ct);
        return token.Success && !string.IsNullOrWhiteSpace(token.Value)
            ? new Dictionary<string, string> { ["GH_TOKEN"] = token.Value.Trim() }
            : null;
    }

    // An ssh alias (git@github-work:org/app.git) names no real host; it is still GitHub.com.
    private static string GitHubHost(ForgeRepoRef repo)
        => repo.Host.Contains('.') ? repo.Host : "github.com";

    private static string GitHubRepoArgument(ForgeRepoRef repo)
        => GitHubHost(repo) == "github.com" ? repo.Slug : $"{GitHubHost(repo)}/{repo.Slug}";

    // ── Azure DevOps ─────────────────────────────────────────────────────────

    private async Task<RunnerOpResult> OpenAzureDevOpsAsync(
        ForgeRepoRef repo, string head, string baseBranch, string title, string body, bool draft, CancellationToken ct)
    {
        var org = AzureOrganizationUrl(repo);
        string[] scope = ["--organization", org, "--project", repo.Project ?? "", "--repository", repo.Name];

        var existing = await RunAsync("az", ["repos", "pr", "list", .. scope, "--source-branch", head,
            "--status", "active", "--query", "[0].pullRequestId", "-o", "tsv"], null, ct);
        if (existing.Error == NotInstalled)
            return RunnerOpResult.Fail("The Azure CLI (az) is not installed on this machine — install it, run `az extension add --name azure-devops` and `az login`.");
        if (!existing.Success)
            return RunnerOpResult.Fail($"The Azure CLI could not reach {org} — run `az login` (and `az extension add --name azure-devops`) on this machine. {existing.Error}");
        if (int.TryParse(existing.Value?.Trim(), out var openId))
            return RunnerOpResult.Ok(AzurePullRequestUrl(repo, openId));

        var create = await RunAsync("az", ForgeCliArgs.AzureCreate(scope, head, baseBranch, title, body, draft), null, ct);
        return create.Success && int.TryParse(create.Value?.Trim(), out var id)
            ? Opened(AzurePullRequestUrl(repo, id), repo)
            : RunnerOpResult.Fail($"az could not open the pull request: {create.Error ?? create.Value}");
    }

    private static string AzureOrganizationUrl(ForgeRepoRef repo)
        => repo.Host.EndsWith(".visualstudio.com", StringComparison.Ordinal)
            ? $"https://{repo.Host}"
            : $"https://dev.azure.com/{repo.Owner}";

    private static string AzurePullRequestUrl(ForgeRepoRef repo, int id)
        => $"{AzureOrganizationUrl(repo)}/{Uri.EscapeDataString(repo.Project ?? "")}/_git/{Uri.EscapeDataString(repo.Name)}/pullrequest/{id}";

    // ── GitLab ───────────────────────────────────────────────────────────────

    private async Task<RunnerOpResult> OpenGitLabAsync(
        ForgeRepoRef repo, string head, string baseBranch, string title, string body, bool draft, CancellationToken ct)
    {
        var existing = await RunAsync("glab", ["mr", "list", "--repo", repo.Slug, "--source-branch", head,
            "--output", "json"], null, ct);
        if (existing.Error == NotInstalled)
            return RunnerOpResult.Fail("The GitLab CLI (glab) is not installed on this machine — install it and run `glab auth login`.");
        if (!existing.Success)
            return RunnerOpResult.Fail($"The GitLab CLI could not reach {repo.Host} — run `glab auth login` on this machine. {existing.Error}");
        if (FirstGitLabUrl(existing.Value) is { } openUrl)
            return RunnerOpResult.Ok(openUrl);

        var create = await RunAsync("glab", ForgeCliArgs.GitLabCreate(repo.Slug, head, baseBranch, title, body, draft), null, ct);
        return create.Success && FirstUrl(create.Value) is { } url
            ? Opened(url, repo)
            : RunnerOpResult.Fail($"glab could not open the merge request: {create.Error ?? create.Value}");
    }

    private static string? FirstGitLabUrl(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                && doc.RootElement.GetArrayLength() > 0
                && doc.RootElement[0].TryGetProperty("web_url", out var url)
                    ? url.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── shared ───────────────────────────────────────────────────────────────

    private RunnerOpResult Opened(string url, ForgeRepoRef repo)
    {
        logger.LogInformation("Opened pull request {Url} on {Repo}", url, repo.Slug);
        return RunnerOpResult.Ok(url);
    }

    internal static string? FirstUrl(string? output)
        => output?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private const string NotInstalled = "not-installed";

    /// <summary>Runs a CLI with an argument LIST (titles and bodies carry quotes and newlines),
    /// stdin closed, prompts impossible, and a two-minute ceiling. A CLI that cannot be started
    /// fails with <see cref="NotInstalled"/> so callers can name the install step.</summary>
    private static async Task<RunnerOpResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return RunnerOpResult.Fail(NotInstalled);
        }
        if (process is null)
            return RunnerOpResult.Fail(NotInstalled);

        using var _ = process;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return RunnerOpResult.Fail($"{fileName} did not answer within two minutes.");
        }

        var output = await stdout;
        var error = (await stderr).Trim();
        return process.ExitCode == 0
            ? RunnerOpResult.Ok(output)
            : RunnerOpResult.Fail(error.Length > 0 ? error : $"{fileName} exited {process.ExitCode}");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp file */ }
    }
}

/// <summary>
/// The forge CLI argument lists — built here, apart from running them, so what Orkka asks each
/// CLI to do (draft, ready, back to draft, a new description) is pinned by tests.
/// </summary>
internal static class ForgeCliArgs
{
    public static string[] GitHubCreate(string repoArg, string head, string baseBranch, string title, string bodyFile, bool draft)
        => ["pr", "create", "--repo", repoArg, "--head", head, "--base", baseBranch,
            "--title", title, "--body-file", bodyFile, .. draft ? new[] { "--draft" } : []];

    public static string[] AzureCreate(string[] scope, string head, string baseBranch, string title, string body, bool draft)
        => ["repos", "pr", "create", .. scope, "--source-branch", head,
            "--target-branch", baseBranch, "--title", title, "--description", body,
            .. draft ? new[] { "--draft", "true" } : [],
            "--query", "pullRequestId", "-o", "tsv"];

    public static string[] GitLabCreate(string slug, string head, string baseBranch, string title, string body, bool draft)
        => ["mr", "create", "--repo", slug, "--source-branch", head,
            "--target-branch", baseBranch, "--title", title, "--description", body,
            .. draft ? new[] { "--draft" } : [], "--yes"];

    /// <summary><c>gh pr ready</c> / <c>gh pr ready --undo</c>.</summary>
    public static string[] GitHubReady(string pullRequestUrl, string repoArg, bool undo)
        => ["pr", "ready", pullRequestUrl, "--repo", repoArg, .. undo ? new[] { "--undo" } : []];

    /// <summary><c>az repos pr update --draft false</c> (ready) / <c>--draft true</c> (back to draft).</summary>
    public static string[] AzureReady(string id, string organizationUrl, bool undo)
        => ["repos", "pr", "update", "--id", id, "--organization", organizationUrl,
            "--draft", undo ? "true" : "false", "-o", "none"];

    /// <summary><c>glab mr update --ready</c> / <c>--draft</c>.</summary>
    public static string[] GitLabReady(string pullRequestUrl, string slug, bool undo)
        => ["mr", "update", pullRequestUrl, "--repo", slug, undo ? "--draft" : "--ready"];

    public static string[] GitHubEditBody(string pullRequestUrl, string repoArg, string bodyFile)
        => ["pr", "edit", pullRequestUrl, "--repo", repoArg, "--body-file", bodyFile];

    public static string[] AzureEditBody(string id, string organizationUrl, string body)
        => ["repos", "pr", "update", "--id", id, "--organization", organizationUrl, "--description", body, "-o", "none"];

    public static string[] GitLabEditBody(string pullRequestUrl, string slug, string body)
        => ["mr", "update", pullRequestUrl, "--repo", slug, "--description", body];
}
