namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Features.Tasks;

/// <summary>
/// The workspace board (<c>GET /api/board</c>): every task of the current workspace across all its
/// projects, the project filter, archived cards, the lane set, per-project keys and isolation.
/// Each test acts inside a throwaway workspace so the shared test org's projects never leak in.
/// </summary>
[Collection("Integration")]
public sealed class WorkspaceBoardEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(ProjectResponse Project, Ulid RepoId)> SeedProject(string name, bool qa = false)
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, "desc"));
        projectResp.StatusCode.Should().Be(HttpStatusCode.Created, await projectResp.Content.ReadAsStringAsync());
        var project = (await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default))!;

        if (qa)
        {
            var update = await _client.PatchAsJsonAsync($"/api/projects/{project.Id}",
                new UpdateProjectRequest(null, null, Settings: project.Settings with { QaEnabled = true }));
            update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        }

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/repos",
            new AddProjectRepoRequest($"{name.ToLowerInvariant().Replace(' ', '-')}-repo", $"https://github.com/test/{Ulid.NewUlid()}.git", "main", null, null));
        var repo = (await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default))!;
        return (project, repo.Id);
    }

    private async Task<TaskResponse> CreateTask(Ulid projectId, Ulid repoId, string title)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, title, "Description", "Medium"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
    }

    private async Task<TaskBoardResponse> GetBoard(string query = "")
    {
        var response = await _client.GetAsync($"/api/board{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TaskBoardResponse>(TestJsonOptions.Default))!;
    }

    private static async Task InWorkspace(Func<Task> body)
    {
        TestCurrentUser.CurrentOrgId = Ulid.NewUlid();
        try
        {
            await body();
        }
        finally
        {
            TestCurrentUser.Reset();
        }
    }

    [Fact]
    public async Task ShowsEveryProjectsTasks_EachCardKeyedAndBadgedByItsOwnProject() => await InWorkspace(async () =>
    {
        var (shop, shopRepo) = await SeedProject("Shop Front");
        var (billing, billingRepo) = await SeedProject("Billing Api");
        var shopTask = await CreateTask(shop.Id, shopRepo, "Checkout button");
        var billingTask = await CreateTask(billing.Id, billingRepo, "Invoice export");

        var board = await GetBoard();
        var cards = board.Lanes.SelectMany(l => l.Tasks).ToList();

        cards.Select(c => c.Id).Should().BeEquivalentTo([shopTask.Id, billingTask.Id]);
        var shopCard = cards.Single(c => c.Id == shopTask.Id);
        shopCard.Code.Should().Be($"{shop.Key}-{shopTask.Number}");
        shopCard.Project.Should().BeEquivalentTo(new BoardProjectRef(shop.Id, shop.Key, shop.Name, shop.Slug, false));
        var billingCard = cards.Single(c => c.Id == billingTask.Id);
        billingCard.Code.Should().Be($"{billing.Key}-{billingTask.Number}");
        billingCard.Project!.Id.Should().Be(billing.Id);
        billingCard.RepoName.Should().NotBeNull("repos are looked up for every project in one batch");
        board.Lanes.Should().NotContain(l => l.Key == "testing", "no project in this workspace tests");
    });

    [Fact]
    public async Task ProjectFilter_NarrowsToOneProject_ByIdOrSlug() => await InWorkspace(async () =>
    {
        var (shop, shopRepo) = await SeedProject("Shop Front");
        var (billing, billingRepo) = await SeedProject("Billing Api");
        var shopTask = await CreateTask(shop.Id, shopRepo, "Checkout button");
        await CreateTask(billing.Id, billingRepo, "Invoice export");

        var byId = await GetBoard($"?projectId={shop.Id}");
        byId.Lanes.SelectMany(l => l.Tasks).Select(c => c.Id).Should().Equal(shopTask.Id);

        var bySlug = await GetBoard($"?projectId={shop.Slug}");
        bySlug.Lanes.SelectMany(l => l.Tasks).Select(c => c.Id).Should().Equal(shopTask.Id);
    });

    [Fact]
    public async Task ArchivedCards_AreHiddenUnlessAskedFor() => await InWorkspace(async () =>
    {
        var (shop, shopRepo) = await SeedProject("Shop Front");
        var archived = await CreateTask(shop.Id, shopRepo, "Old work");
        var live = await CreateTask(shop.Id, shopRepo, "New work");
        (await _client.PostAsync($"/api/tasks/{archived.Id}/archive", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetBoard()).Lanes.SelectMany(l => l.Tasks).Select(c => c.Id).Should().Equal(live.Id);

        var withArchived = (await GetBoard("?includeArchived=true")).Lanes.SelectMany(l => l.Tasks).ToList();
        withArchived.Select(c => c.Id).Should().BeEquivalentTo([archived.Id, live.Id]);
        withArchived.Single(c => c.Id == archived.Id).IsArchived.Should().BeTrue();
    });

    [Fact]
    public async Task TestingLane_ShowsWhenAnyProjectTests_AndCardsSayWhichDo() => await InWorkspace(async () =>
    {
        var (plain, plainRepo) = await SeedProject("Plain Site");
        var (tested, testedRepo) = await SeedProject("Tested App", qa: true);
        var plainTask = await CreateTask(plain.Id, plainRepo, "No QA here");
        var testedTask = await CreateTask(tested.Id, testedRepo, "QA here");

        var all = await GetBoard();
        all.Lanes.Select(l => l.Key).Should().Equal(
            "drafting", "ready", "planning", "plan_ready", "pending_questions", "developing", "testing", "code_review", "done");
        var cards = all.Lanes.SelectMany(l => l.Tasks).ToList();
        cards.Single(c => c.Id == plainTask.Id).Project!.HasTesting.Should().BeFalse();
        cards.Single(c => c.Id == testedTask.Id).Project!.HasTesting.Should().BeTrue();

        (await GetBoard($"?projectId={plain.Id}")).Lanes.Should().NotContain(l => l.Key == "testing",
            "narrowed to a project that does not test, the board is that project's board");
        (await GetBoard($"?projectId={tested.Id}")).Lanes.Should().Contain(l => l.Key == "testing");
    });

    [Fact]
    public async Task BlockerRefs_UseTheBlockersOwnProjectKey() => await InWorkspace(async () =>
    {
        await SeedProject("Shop Front");
        var (billing, billingRepo) = await SeedProject("Billing Api");
        var first = await CreateTask(billing.Id, billingRepo, "Schema");
        var second = await CreateTask(billing.Id, billingRepo, "Endpoint");
        var dep = await _client.PostAsJsonAsync($"/api/tasks/{second.Id}/dependencies", new AddTaskDependencyRequest(first.Id));
        dep.IsSuccessStatusCode.Should().BeTrue(await dep.Content.ReadAsStringAsync());

        var card = (await GetBoard()).Lanes.SelectMany(l => l.Tasks).Single(c => c.Id == second.Id);
        card.HasUnmetDependencies.Should().BeTrue();
        card.BlockedBy.Should().ContainSingle().Which.Code.Should().Be($"{billing.Key}-{first.Number}");
    });

    [Fact]
    public async Task AnotherWorkspacesProjectsAndTasks_NeverShow_AndFilteringOnOneIs404()
    {
        ProjectResponse foreign = null!;
        Ulid foreignTaskId = default;
        await InWorkspace(async () =>
        {
            var (project, repo) = await SeedProject("Other Employer");
            foreign = project;
            foreignTaskId = (await CreateTask(project.Id, repo, "Not mine here")).Id;
        });

        await InWorkspace(async () =>
        {
            var (mine, mineRepo) = await SeedProject("My Job");
            var myTask = await CreateTask(mine.Id, mineRepo, "Mine");

            var cards = (await GetBoard()).Lanes.SelectMany(l => l.Tasks).ToList();
            cards.Select(c => c.Id).Should().Equal(myTask.Id);
            cards.Should().NotContain(c => c.Id == foreignTaskId);

            (await _client.GetAsync($"/api/board?projectId={foreign.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await _client.GetAsync($"/api/board?projectId={foreign.Slug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await _client.GetAsync($"/api/board?projectId={Ulid.NewUlid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await _client.GetAsync($"/api/board/search?q=Not&projectId={foreign.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await _client.GetFromJsonAsync<List<TaskSummaryResponse>>("/api/board/search?q=Not%20mine", TestJsonOptions.Default))
                .Should().BeEmpty("a search never reaches into another workspace");
        });
    }

    [Fact]
    public async Task AWorkspaceWithoutProjects_HasAnEmptyBoard() => await InWorkspace(async () =>
    {
        var board = await GetBoard();
        board.Lanes.Should().NotBeEmpty();
        board.Lanes.SelectMany(l => l.Tasks).Should().BeEmpty();
    });

    [Fact]
    public async Task Search_SpansTheWorkspace_AndHonoursTheProjectFilter() => await InWorkspace(async () =>
    {
        var (shop, shopRepo) = await SeedProject("Shop Front");
        var (billing, billingRepo) = await SeedProject("Billing Api");
        var shopTask = await CreateTask(shop.Id, shopRepo, "Export the cart");
        var billingTask = await CreateTask(billing.Id, billingRepo, "Export invoices");

        var all = await _client.GetFromJsonAsync<List<TaskSummaryResponse>>("/api/board/search?q=export", TestJsonOptions.Default);
        all!.Select(c => c.Id).Should().BeEquivalentTo([shopTask.Id, billingTask.Id]);
        all.Single(c => c.Id == billingTask.Id).Code.Should().Be($"{billing.Key}-{billingTask.Number}");

        var narrowed = await _client.GetFromJsonAsync<List<TaskSummaryResponse>>(
            $"/api/board/search?q=export&projectId={shop.Slug}", TestJsonOptions.Default);
        narrowed!.Select(c => c.Id).Should().Equal(shopTask.Id);
    });
}
