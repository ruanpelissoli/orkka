namespace Orkka.IntegrationTests.Fixtures;

using System.Security.Cryptography;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The Orkka host (<see cref="Orkka.Local.OrkkaHost"/>) on a test server, with a fresh SQLite
/// file per factory. Built lazily on first use of <see cref="Services"/> or a client, so a test
/// can set <see cref="ConfigureTestServices"/> or <see cref="Settings"/> first.
/// </summary>
public sealed class OrkkaApiFactory : IAsyncLifetime, IAsyncDisposable
{
    /// <summary>The install secret every client presents (<c>Local:InstallSecret</c>).</summary>
    public const string InstallSecret = "test-install-secret";

    /// <summary>The <c>/internal/*</c> relay key (<c>WorkerAuth:ApiKey</c>).</summary>
    public const string WorkerApiKey = "test-worker-api-key-12345";

    /// <summary>The host's port: <see cref="Orkka.Local.Auth.LocalRequestGuard"/> refuses any other
    /// Host header with 421, so every client targets 127.0.0.1 on it.</summary>
    public const int Port = 6750;

    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "orkka-integration-" + Guid.NewGuid().ToString("N"));

    private static readonly string TestEncryptionKey =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly Lock _gate = new();
    private WebApplication? _app;

    /// <summary>
    /// Optional callback to configure test-specific service overrides.
    /// Set before calling CreateClient() to override services like IPullRequestService.
    /// </summary>
    public Action<IServiceCollection>? ConfigureTestServices { get; set; }

    /// <summary>Extra host settings (e.g. <c>Local:UiDir</c>); set before first use.</summary>
    public Dictionary<string, string?> Settings { get; } = new();

    public string DataDir => _dataDir;

    public IServiceProvider Services => EnsureStarted().Services;

    /// <summary>A client that talks to the host the way the CLI does: an allowlisted Host and
    /// the install-secret bearer. Follows redirects and keeps cookies.</summary>
    public HttpClient CreateClient() => CreateClient(new WebApplicationFactoryClientOptions());

    public HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        var server = EnsureStarted().Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("The host is not on a test server.");
        HttpMessageHandler handler = server.CreateHandler();
        if (options.HandleCookies)
            handler = new CookieContainerHandler { InnerHandler = handler };
        if (options.AllowAutoRedirect)
            handler = new RedirectHandler(options.MaxAutomaticRedirections) { InnerHandler = handler };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{Port}") };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", InstallSecret);
        return client;
    }

    private WebApplication EnsureStarted()
    {
        lock (_gate)
        {
            if (_app is not null)
                return _app;

            var settings = new Dictionary<string, string?>
            {
                ["Encryption:Key"] = TestEncryptionKey,
                [Orkka.Local.LocalSettings.InstallSecretKey] = InstallSecret,
                ["Local:Port"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                // The database is {DataDir}/orkka.db: one SQLite file per factory, deleted on dispose.
                ["Local:DataDir"] = _dataDir,
                // No built UI unless a test points at one (StaticUiTests).
                ["Local:UiDir"] = Path.Combine(_dataDir, "no-ui"),
                // The suite pins the relay key so a test can call an /internal relay itself.
                [Orkka.Local.OrkkaHost.WorkerKeySetting] = WorkerApiKey,
                // The host points the API→runner relays at its own loopback port, which a
                // TestServer does not listen on. Unset, every relay reads as "no runner".
                [Orkka.Local.OrkkaHost.RelayUrlSetting] = "",
                ["Anthropic:DefaultModel"] = "claude-sonnet-5",
                // Tools-enabled for the whole suite: turns carry the orkka tool server
                // (FakeChatAgentInvoker reports SupportsChatTools), while tests that never
                // script a tool call keep exercising the fenced-block fallback path.
                ["AgentChat:Tools:Enabled"] = "true",
            };
            foreach (var (key, value) in Settings)
                settings[key] = value;

            var app = Orkka.Local.OrkkaHost.BuildAsync(new Orkka.Local.OrkkaHostOptions
            {
                EnvironmentName = "Testing",
                Settings = settings,
                ConfigureBuilder = builder => builder.WebHost.UseTestServer(),
                ConfigureServices = ConfigureServices,
            }).GetAwaiter().GetResult();
            app.StartAsync().GetAwaiter().GetResult();
            _app = app;
            return app;
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Register test ICurrentUser
        services.AddScoped<ICurrentUser, TestCurrentUser>();

        // Requests authenticate exactly as the CLI does: the install-secret bearer (see
        // CreateClient). Identity then comes from TestCurrentUser, registered above.

        // Replace IBlobStorageService with mock
        var blobDescriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IBlobStorageService));
        if (blobDescriptor is not null)
            services.Remove(blobDescriptor);
        services.AddSingleton<IBlobStorageService, MockBlobStorageService>();

        // Replace ITaskNotifier with mock
        var notifierDescriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(ITaskNotifier));
        if (notifierDescriptor is not null)
            services.Remove(notifierDescriptor);
        services.AddSingleton<ITaskNotifier, MockTaskNotifier>();

        // Replace IChatAgentInvoker with the controllable fake — the real one spawns
        // the claude CLI.
        var chatInvokerDescriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(Orkka.Modules.Agents.Contracts.IChatAgentInvoker));
        if (chatInvokerDescriptor is not null)
            services.Remove(chatInvokerDescriptor);
        services.AddSingleton<FakeChatAgentInvoker>();
        services.AddSingleton<Orkka.Modules.Agents.Contracts.IChatAgentInvoker>(
            sp => sp.GetRequiredService<FakeChatAgentInvoker>());

        // Apply any test-specific overrides
        ConfigureTestServices?.Invoke(services);
    }

    public async Task InitializeAsync()
    {
        // Resolving Services starts the host, which applies every module's migrations to the
        // fresh database file first.
        using var scope = Services.CreateScope();
        var orgDb = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
        var agentsDb = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        // The model catalog is seeded by a startup hosted service that races this fixture;
        // seed it here so every test sees it (idempotent).
        await Orkka.Modules.Agents.Infrastructure.Services.AiModelSeeder
            .SeedAsync(agentsDb, CancellationToken.None);

        // Seed organization and member for CurrentUserMiddleware lookups
        var org = new Organization
        {
            Id = TestCurrentUser.TestOrgId,
            Name = "Test Organization",
            Slug = "test-org"
        };
        orgDb.Organizations.Add(org);

        var member = new OrgMember
        {
            Id = TestCurrentUser.TestMemberId,
            OrgId = TestCurrentUser.TestOrgId,
            UserId = TestCurrentUser.TestUserId,
            Role = OrgRole.Owner
        };
        orgDb.OrgMembers.Add(member);

        await orgDb.SaveChangesAsync();
    }

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        WebApplication? app;
        lock (_gate)
        {
            app = _app;
            _app = null;
        }
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        // Pooled connections keep the database file open (and locked on Windows).
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // A handle still closing; the temp folder is the OS's to reap.
        }
    }
}
