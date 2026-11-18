namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Runners.Features.LocalRepos;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Base-folder projects end to end through the host, with the runner faked at the loopback
/// relay: create a project from a folder (remote and local-folder repos, the base folder and
/// shape recorded, mirrors primed with the checkout path), and rescan it (new, vanished,
/// reappearing and re-pointed repos; in-flight tasks noted, never stopped).
/// </summary>
[Collection("Integration")]
public sealed class BaseFolderProjectTests : IAsyncLifetime
{
    private const string Base = @"C:\work\shop";

    private readonly OrkkaApiFactory _factory = new();
    private readonly FakeRunnerRelay _runner = new();
    private HttpClient _client = null!;
    private bool _online = true;

    public async Task InitializeAsync()
    {
        _factory.ConfigureTestServices = services =>
        {
            services.RemoveAll<IRunnerRelayLocator>();
            services.AddSingleton<IRunnerRelayLocator>(new FakeLocator(() => _online));
            services.AddHttpClient(RunnerRelay.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => _runner);
        };
        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
        RescanProjectEndpoint.ResetDebounce();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static object Repo(string path, string? origin, bool dirty = false, bool hasCommits = true,
        string? description = null) => new
    {
        relativePath = path,
        name = path.Length == 0 ? "shop" : path.Split('/')[^1],
        originUrl = origin,
        defaultBranch = "main",
        currentBranch = "main",
        isDirty = dirty,
        hasUnpushedCommits = false,
        hasCommits,
        description,
        stack = "node",
    };

    private void Discovers(string shape, params object[] repos) =>
        _runner.Discover = new { supported = true, path = Base, shape, repos, areas = Array.Empty<string>(), error = (string?)null };

    private async Task<CreateProjectFromFolderResponse> CreateAsync(string? name = null)
    {
        var response = await _client.PostAsJsonAsync("/api/projects/from-folder",
            new CreateProjectFromFolderRequest(Base, name));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreateProjectFromFolderResponse>(TestJsonOptions.Default))!;
    }

    private async Task<RescanProjectResponse> RescanAsync(Ulid projectId, bool auto = false)
    {
        var response = await _client.PostAsync($"/api/projects/{projectId}/rescan?auto={auto.ToString().ToLowerInvariant()}", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RescanProjectResponse>(TestJsonOptions.Default))!;
    }

    [Fact]
    public async Task Discover_RelaysThePreview_WithLocationAndForge()
    {
        Discovers("multirepo", Repo("api", "https://github.com/acme/api.git"), Repo("web", null));

        var response = await _client.PostAsJsonAsync("/api/runners/fs/discover", new DiscoverReposRequestDto(Base));
        var body = await response.Content.ReadFromJsonAsync<DiscoverReposResponseDto>(TestJsonOptions.Default);

        body!.Supported.Should().BeTrue();
        body.Shape.Should().Be("multirepo");
        var api = body.Repos.Single(r => r.RelativePath == "api");
        api.Location.Should().Be("remote");
        api.Forge.Should().Be("github");
        api.Host.Should().Be("github.com");
        body.Repos.Single(r => r.RelativePath == "web").Location.Should().Be("local");
    }

    [Fact]
    public async Task FromFolder_RegistersRemotesAndFolders_AndPrimesMirrorsFromTheCheckout()
    {
        Discovers("multirepo",
            Repo("api", "https://github.com/acme/api.git", description: "Orders API"),
            Repo("web", null, dirty: true),
            Repo("empty", null, hasCommits: false));

        var created = await CreateAsync();

        created.Project.Name.Should().Be("shop");
        created.Project.BaseFolder.Should().Be(Base);
        created.Project.BaseFolderShape.Should().Be("multirepo");
        created.Repos.Should().HaveCount(2);

        var api = created.Repos.Single(r => r.Name == "api");
        api.GitUrl.Should().Be("https://github.com/acme/api.git");
        api.Location.Should().Be("remote");
        api.FolderPath.Should().Be("api");
        api.Description.Should().Be("Orders API");
        api.WorkspaceStatus.Should().Be("ready");

        var web = created.Repos.Single(r => r.Name == "web");
        web.GitUrl.Should().Be(@"C:\work\shop\web");
        web.Location.Should().Be("local");

        created.Warnings.Should().Contain(w => w.Contains("empty") && w.Contains("no commits"));
        created.Warnings.Should().Contain(w => w.Contains("web") && w.Contains("uncommitted"));

        _runner.EnsureClones.Should().Contain(c =>
            c.GetProperty("cloneUrlOrPath").GetString() == "https://github.com/acme/api.git"
            && c.GetProperty("localCheckoutPath").GetString() == @"C:\work\shop\api");

        // The project read model carries the base folder; the repo list carries the new fields.
        var project = await _client.GetFromJsonAsync<ProjectResponse>($"/api/projects/{created.Project.Id}", TestJsonOptions.Default);
        project!.BaseFolder.Should().Be(Base);
        project.BaseFolderShape.Should().Be("multirepo");
        var repos = await _client.GetFromJsonAsync<List<ProjectRepoResponse>>($"/api/projects/{created.Project.Id}/repos", TestJsonOptions.Default);
        repos!.Single(r => r.Name == "web").Location.Should().Be("local");
        repos.Single(r => r.Name == "api").FolderPath.Should().Be("api");
    }

    [Fact]
    public async Task FromFolder_Monorepo_IsOneRepoAtTheBaseFolder()
    {
        Discovers("monorepo", Repo("", "git@github.com:acme/shop.git"));

        var created = await CreateAsync("Shop");

        created.Project.Name.Should().Be("Shop");
        created.Project.BaseFolderShape.Should().Be("monorepo");
        created.Repos.Should().ContainSingle().Which.FolderPath.Should().Be("");
    }

    [Fact]
    public async Task FromFolder_RunnerOffline_NamesOrkkaLocalStart()
    {
        _online = false;

        var response = await _client.PostAsJsonAsync("/api/projects/from-folder", new CreateProjectFromFolderRequest(Base, null));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("./orkka start");
    }

    [Fact]
    public async Task Rescan_AddsMarksMissingRestoresAndFollowsAChangedOrigin()
    {
        Discovers("multirepo", Repo("api", "https://github.com/acme/api.git"), Repo("web", null));
        var created = await CreateAsync();
        var apiId = created.Repos.Single(r => r.Name == "api").Id;
        var webId = created.Repos.Single(r => r.Name == "web").Id;

        // web vanished, admin appeared, api's origin moved.
        Discovers("multirepo", Repo("api", "https://github.com/acme/orders-api.git"), Repo("admin", null));
        var first = await RescanAsync(created.Project.Id);

        first.Added.Should().Equal("admin");
        first.Missing.Should().Equal("web");
        first.OriginUpdated.Should().Equal("api");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProjectsDbContext>();
            var repos = await db.ProjectRepos.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.ProjectId == created.Project.Id).ToListAsync();
            repos.Single(r => r.Id == webId).MissingSince.Should().NotBeNull();
            repos.Single(r => r.Id == apiId).GitUrl.Should().Be("https://github.com/acme/orders-api.git");
        }

        // web came back.
        Discovers("multirepo", Repo("api", "https://github.com/acme/orders-api.git"), Repo("admin", null), Repo("web", null));
        var second = await RescanAsync(created.Project.Id);

        second.Restored.Should().Equal("web");
        second.Added.Should().BeEmpty();
        second.Repos.Should().HaveCount(3);
    }

    [Fact]
    public async Task Rescan_WithATaskInFlight_KeepsTheOriginAndNotesAVanishedRepo()
    {
        Discovers("multirepo", Repo("api", "https://github.com/acme/api.git"), Repo("web", null));
        var created = await CreateAsync();
        var apiId = created.Repos.Single(r => r.Name == "api").Id;
        var webId = created.Repos.Single(r => r.Name == "web").Id;
        var apiTask = await SeedActiveTaskAsync(created.Project.Id, apiId);
        var webTask = await SeedActiveTaskAsync(created.Project.Id, webId);

        Discovers("multirepo", Repo("api", "https://github.com/acme/orders-api.git"));
        var result = await RescanAsync(created.Project.Id);

        result.OriginUpdated.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("api") && w.Contains("in flight"));
        result.Missing.Should().Equal("web");

        await using var scope = _factory.Services.CreateAsyncScope();
        var tasks = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        (await tasks.TaskMessages.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(m => m.TaskId == webTask && m.Content.Contains("no longer at"))).Should().BeTrue();
        (await tasks.Tasks.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == webTask))
            .Status.Should().Be(TaskStatus.Active, "a vanished folder never stops a task");
        var projects = scope.ServiceProvider.GetRequiredService<ProjectsDbContext>();
        (await projects.ProjectRepos.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == apiId))
            .GitUrl.Should().Be("https://github.com/acme/api.git");
        apiTask.Should().NotBe(default(Ulid));
    }

    [Fact]
    public async Task AutoRescan_IsDebounced_AndSkipsQuietlyWhenOffline()
    {
        Discovers("multirepo", Repo("api", "https://github.com/acme/api.git"));
        var created = await CreateAsync();

        (await RescanAsync(created.Project.Id, auto: true)).Skipped.Should().BeFalse();
        var discoverCalls = _runner.DiscoverCalls;
        (await RescanAsync(created.Project.Id, auto: true)).Skipped.Should().BeTrue();
        _runner.DiscoverCalls.Should().Be(discoverCalls, "the second open within ten minutes does not ask the runner");

        _online = false;
        RescanProjectEndpoint.ResetDebounce();
        var offline = await RescanAsync(created.Project.Id, auto: true);
        offline.Skipped.Should().BeTrue();
        offline.Offline.Should().BeTrue();

        var manual = await _client.PostAsync($"/api/projects/{created.Project.Id}/rescan", null);
        manual.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task RepoDescription_IsEditable()
    {
        Discovers("multirepo", Repo("api", "https://github.com/acme/api.git"));
        var created = await CreateAsync();
        var api = created.Repos.Single();

        var response = await _client.PatchAsJsonAsync($"/api/projects/{created.Project.Id}/repos/{api.Id}",
            new UpdateProjectRepoRequest("main", Description: "Orders and invoices HTTP API"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default))!
            .Description.Should().Be("Orders and invoices HTTP API");
    }

    private static int _nextNumber = 40_000;

    private async Task<Ulid> SeedActiveTaskAsync(Ulid projectId, Ulid repoId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            ProjectId = projectId,
            RepoId = repoId,
            Number = Interlocked.Increment(ref _nextNumber),
            Title = "In flight",
            CurrentLane = Lane.Developing,
            Status = TaskStatus.Active,
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    private sealed class FakeLocator(Func<bool> online) : IRunnerRelayLocator
    {
        public Task<RunnerRelayTarget?> LocateAsync(Ulid orgId, string userId, CancellationToken ct = default)
            => Task.FromResult(online() ? new RunnerRelayTarget(orgId, userId, "http://runner.test") : null);
    }

    /// <summary>The runner as the loopback relay sees it: discover answers whatever the test
    /// set, ensure-clone succeeds and records what it was asked.</summary>
    private sealed class FakeRunnerRelay : HttpMessageHandler
    {
        public object? Discover { get; set; }
        public int DiscoverCalls { get; private set; }
        public List<JsonElement> EnsureClones { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            object answer = path switch
            {
                "/internal/runner/fs/discover" => Count(Discover!),
                "/internal/runner/repo/ensure-clone" => Record(body),
                _ => throw new InvalidOperationException($"Unexpected relay call {path}"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(answer) };
        }

        private object Count(object discover)
        {
            DiscoverCalls++;
            return discover;
        }

        private object Record(string body)
        {
            lock (EnsureClones)
                EnsureClones.Add(JsonSerializer.Deserialize<JsonElement>(body));
            return new { supported = true, success = true, value = "bare", error = (string?)null };
        }
    }
}
