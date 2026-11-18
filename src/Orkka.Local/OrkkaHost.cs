namespace Orkka.Local;

using System.Net;
using System.Security.Cryptography;
using FastEndpoints;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Orkka.Local.Auth;
using Orkka.Local.Infrastructure;
using Orkka.Local.Storage;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Features.Mcp;
using Orkka.Modules.Tasks;
using Orkka.Runners.Server.Hosting;
using Serilog;

/// <summary>How <see cref="OrkkaHost.BuildAsync"/> builds the host.</summary>
public sealed class OrkkaHostOptions
{
    /// <summary>Command-line style configuration (rarely needed).</summary>
    public string[] Args { get; init; } = [];

    /// <summary>ASP.NET Core environment; <c>Production</c> unless a test says otherwise.</summary>
    public string EnvironmentName { get; init; } = Environments.Production;

    /// <summary>Where <c>appsettings.json</c> is read from; the app's base directory by default.</summary>
    public string? ContentRoot { get; init; }

    /// <summary>
    /// In-memory configuration layered over appsettings and the environment — how the secrets
    /// reach the host (<see cref="LocalSettings.InstallSecretKey"/>, <c>Encryption:Key</c>) without
    /// ever entering the process environment, plus <c>Local:Port</c>, <c>Local:DataDir</c>,
    /// <c>Local:UiDir</c>. <c>WorkerAuth:ApiKey</c> and <c>Runners:RelayUrlTemplate</c> are
    /// generated unless given here.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

    /// <summary>Runs on the builder before anything is registered (e.g. a test server).</summary>
    public Action<WebApplicationBuilder>? ConfigureBuilder { get; init; }

    /// <summary>Runs after every Orkka service is registered (test overrides).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>Adds sinks to the host's Serilog logger (console, the log file).</summary>
    public Action<LoggerConfiguration>? ConfigureLogging { get; init; }
}

/// <summary>
/// Builds the Orkka host — the API, the background engine (task queue, autopilot, planning),
/// the browsers' TaskHub, the runner's hub and the static web UI — bound to 127.0.0.1 only.
/// <c>./orkka start</c> (src/Orkka) runs it in its own process next to the runner; the
/// integration tests build it with a test server.
/// </summary>
public static class OrkkaHost
{
    public const string WorkerKeySetting = "WorkerAuth:ApiKey";
    public const string RelayUrlSetting = "Runners:RelayUrlTemplate";

    /// <summary>Builds the host and applies every module's migrations; not started yet.</summary>
    public static async Task<WebApplication> BuildAsync(OrkkaHostOptions options, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = options.Args,
            EnvironmentName = options.EnvironmentName,
            ContentRootPath = options.ContentRoot ?? AppContext.BaseDirectory,
        });
        builder.Configuration.AddInMemoryCollection(options.Settings);
        options.ConfigureBuilder?.Invoke(builder);

        var settings = LocalSettings.From(builder.Configuration);

        // Loopback only — this host is never reachable from another machine.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, settings.Port));

        // Before any module registration (several read these while composing):
        // - the key protecting secrets at rest (handed over in memory by `./orkka start`);
        LocalEncryptionKey.Require(builder.Configuration);
        // - the database: {DataDir}/orkka.db unless a connection string names another file;
        if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("DefaultConnection")))
            builder.Configuration["ConnectionStrings:DefaultConnection"] =
                Orkka.SharedKernel.Persistence.SqliteDatabase.ConnectionStringFor(settings.DatabasePath);
        // - the key the in-process relays present on /internal/* — fresh per process, held in
        //   memory only, so nothing outside this process can call the relays;
        // - the API→runner relays (repo reads, local-folder git, chat turns, app runs) loop back
        //   into this same process, which holds the runner's socket.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WorkerKeySetting] = options.Settings.TryGetValue(WorkerKeySetting, out var key) && key is not null
                ? key
                : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            [RelayUrlSetting] = options.Settings.TryGetValue(RelayUrlSetting, out var relay) && relay is not null
                ? relay
                : settings.LoopbackBaseUrl,
        });

        builder.Host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services);
            options.ConfigureLogging?.Invoke(configuration);
        });
        builder.Host.ConfigureHostOptions(hostOptions =>
            hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Local:ShutdownTimeoutSeconds", 10)));

        builder.Services.AddOrkkaLocal(builder.Configuration, settings);
        options.ConfigureServices?.Invoke(builder.Services);

        var app = builder.Build();

        // No separate migrator on a developer's machine: every module migrates the one file, in turn.
        await app.Services.ApplyModuleMigrationsAsync(ct);

        app.UseMiddleware<LocalRequestGuard>();
        app.UseCorrelationId();
        app.UseExceptionHandler();
        app.UseSerilogRequestLogging(o =>
            o.GetLevel = (httpContext, _, exception) =>
                exception is not null || httpContext.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
                : httpContext.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Verbose
                : Serilog.Events.LogEventLevel.Information);
        // Everything that is not the host's own is the web UI; pages need a session (the guard
        // above already applied the Host and Origin rules).
        app.UseStaticUi();
        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseCurrentUser();
        app.UseFastEndpoints(config =>
        {
            config.Serializer.Options.Converters.Add(new UlidJsonConverter());
        });

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = HealthResponse.WriteAsync,
            ResultStatusCodes = HealthResponse.StatusCodes,
        }).AllowAnonymous();
        app.MapTasksModule();                                   // /hubs/tasks
        app.MapHub<HostedRunnerHub>(RunnerProtocol.HubPath);    // /hubs/runner (orkr_ tokens)
        app.MapRunnerRelay();                                   // /internal/runner/*
        app.MapLocalFolderRelay();                              // /internal/runner/local/*
        app.MapOrkkaMcp();                                      // /mcp (orkka_key_ keys)
        app.MapLocalBlobs();                                    // /blobs/*
        app.MapLocalEndpoints();                                // /launch, /local/*

        return app;
    }
}
