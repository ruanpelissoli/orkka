namespace Orkka.Cli;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkka.Local;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Cli;
using Orkka.LocalRunner.Runtime;
using Orkka.LocalRunner.Trackers;
using Serilog;
using Serilog.Events;

/// <summary>
/// <c>./orkka start [--port N] [--no-browser]</c> — runs Orkka in THIS process, in the
/// foreground: the web host (API, background engine, hubs, the built UI) on 127.0.0.1:port only,
/// and the runner, which dials that host's <c>/hubs/runner</c> over loopback with its
/// <c>orkr_</c> tokens exactly as a second machine's runner would. Ctrl+C (or <c>./orkka stop</c>)
/// stops the runner first, then the host.
/// </summary>
public static class StartCommand
{
    public const string NoBrowserFlag = "--no-browser";
    public const string PortFlag = "--port";

    private static readonly TimeSpan BootstrapInterval = TimeSpan.FromSeconds(30);

    public sealed record Options(int Port, bool OpenBrowser);

    /// <summary>Parses <c>--port N</c> and <c>--no-browser</c>; null with a message on a bad value.</summary>
    public static Options? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var port = LocalSettings.DefaultPort;
        var openBrowser = true;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals(NoBrowserFlag, StringComparison.OrdinalIgnoreCase))
                openBrowser = false;
            else if (arg.Equals(PortFlag, StringComparison.OrdinalIgnoreCase) || arg.StartsWith(PortFlag + "=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : i + 1 < args.Count ? args[++i] : "";
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1024 or > 65535)
                {
                    error = $"--port needs a number between 1024 and 65535 (got '{value}').";
                    return null;
                }
            }
            else
            {
                error = $"Unknown option '{arg}' for start. Usage: ./orkka start [--port N] [--no-browser]";
                return null;
            }
        }
        return new Options(port, openBrowser);
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, bool debug)
    {
        if (Parse(args, out var parseError) is not { } options)
        {
            Console.Error.WriteLine(parseError);
            return 2;
        }

        var home = RunnerEnvFile.OrkkaDir;
        // Before anything reads the secret store or the scrub deletes it (it names a SECRET).
        WorkspaceSecretStores.CaptureOverride();
        ConsoleBanner.Print();

        // ── One Orkka per Orkka home, one listener per port ─────────────────────────
        if (OrkkaInstance.ReadAlive(home) is { } running)
        {
            Console.Error.WriteLine($"Orkka is already running at {running.Url} (pid {running.Pid}).");
            Console.Error.WriteLine("Open it with `./orkka open`, or stop it with `./orkka stop`.");
            return 1;
        }
        using var instanceLock = OrkkaInstance.TryLock(home);
        if (instanceLock is null)
        {
            Console.Error.WriteLine($"Another Orkka is starting or running from {home}.");
            Console.Error.WriteLine("Open it with `./orkka open`, or stop it with `./orkka stop`.");
            return 1;
        }
        if (!IsPortFree(options.Port))
        {
            using var probe = new LocalHostClient(options.Port, installSecret: null);
            if (await probe.IsOrkkaAsync(CancellationToken.None))
            {
                Console.Error.WriteLine($"Another Orkka (with a different ORKKA_HOME) is already running on port {options.Port}.");
                Console.Error.WriteLine("Stop it with `./orkka stop` from its environment, or start this one with `--port <another port>`.");
            }
            else
            {
                Console.Error.WriteLine($"Port {options.Port} on 127.0.0.1 is in use by another program.");
                Console.Error.WriteLine("Free it, or start Orkka on another port: `./orkka start --port 6760`.");
            }
            return 1;
        }

        // ── Secrets: the OS secret store, handed to the host in memory ──────────────
        LocalStackSecrets secrets;
        try
        {
            secrets = LocalSecrets.Open(home).EnsureAll();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read or create Orkka's secrets in the OS secret store: {ex.Message}");
            return 1;
        }

        Directory.CreateDirectory(OrkkaInstance.LogsDir(home));
        WebApplication app;
        try
        {
            Console.WriteLine("Starting Orkka...");
            app = await OrkkaHost.BuildAsync(new OrkkaHostOptions
            {
                Settings = HostSettings(options.Port, secrets),
                ConfigureLogging = log => ConfigureLogging(log, home, debug),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Orkka could not start: {ex.Message}");
            Console.Error.WriteLine($"Details: {OrkkaInstance.LogFile(home)}");
            return 1;
        }

        await using (app)
        {
            // The host has read its configuration; from here on nothing sensitive stays in this
            // process's environment, which every agent the runner spawns inherits.
            var machine = RunnerProcess.PrepareEnvironment(Console.WriteLine);

            try
            {
                await app.StartAsync();
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Orkka could not listen on 127.0.0.1:{options.Port}: {ex.Message}");
                Console.Error.WriteLine("Start it on another port: `./orkka start --port 6760`.");
                return 1;
            }

            using var self = Process.GetCurrentProcess();
            var info = new InstanceInfo(Environment.ProcessId, options.Port, DateTimeOffset.UtcNow,
                OrkkaInstance.StartTimeOf(self) ?? DateTimeOffset.UtcNow);
            OrkkaInstance.Write(home, info);

            var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            using var client = new LocalHostClient(options.Port, secrets.InstallSecret);
            IHost? runner = null;
            try
            {
                // ── The runner, in this process, dialing the host over loopback ─────
                await BootstrapAsync(client, home, stopping.Token);
                var profiles = ConnectCommand.Resolve([ConnectCommand.AllowEmptyFlag], home,
                    out var connectError, out var connectNotice) ?? [];
                if (connectNotice is not null)
                    Console.WriteLine(connectNotice);
                if (connectError is not null)
                    Console.WriteLine(connectError);

                runner = RunnerHost.Build(new RunnerHostOptions
                {
                    Machine = machine,
                    Profiles = profiles,
                    Debug = debug,
                    ExternallyStopped = true,
                    // The host is in this process: the "ready"/"reconnecting" narration meant for
                    // a runner on another machine would only repeat (or misreport) what start says.
                    ConsoleStatus = false,
                    ConfigureLogging = logging => logging.AddSerilog(Log.Logger, dispose: false),
                });
                await runner.StartAsync(stopping.Token);

                Console.WriteLine();
                Console.WriteLine($"Orkka is running at {info.Url}");
                await OpenBrowserAsync(client, options.OpenBrowser, stopping.Token);
                Console.WriteLine("Press Ctrl+C to stop (or run `./orkka stop` from another terminal).");
                Console.WriteLine($"Logs: {OrkkaInstance.LogFile(home)}");

                // Every 30 s: a workspace created in the UI gets a runner token and profile, which
                // the runner picks up by itself.
                while (!stopping.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(BootstrapInterval, stopping.Token);
                        await BootstrapAsync(client, home, stopping.Token);
                    }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
                    {
                        Log.Warning("Runner bootstrap failed (will retry): {Error}", ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // Stopped while starting.
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                Console.Error.WriteLine($"Orkka could not start the runner: {ex.Message}");
                Environment.ExitCode = 1;
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine("Stopping Orkka...");
                // The runner first: its sessions finish talking to the host that is still up.
                if (runner is not null)
                {
                    try { await runner.StopAsync(); }
                    catch (Exception ex) { Log.Warning("Stopping the runner: {Error}", ex.Message); }
                    runner.Dispose();
                }
                // No agent outlives Orkka, even when the runner's stop ran out of time.
                Orkka.Modules.Execution.Agents.AgentProcesses.KillAll();
                await app.StopAsync();
                OrkkaInstance.Delete(home);
                Console.WriteLine("Orkka stopped.");
                await Log.CloseAndFlushAsync();
            }
        }
        return Environment.ExitCode;
    }

    /// <summary>What the host gets in memory — never through the environment.</summary>
    internal static Dictionary<string, string?> HostSettings(int port, LocalStackSecrets secrets) => new()
    {
        ["Local:Port"] = port.ToString(CultureInfo.InvariantCulture),
        [LocalSettings.InstallSecretKey] = secrets.InstallSecret,
        ["Encryption:Key"] = secrets.EncryptionKey,
    };

    private static void ConfigureLogging(LoggerConfiguration log, string home, bool debug)
    {
        // Everything (Information and up) to ~/.orkka/logs/orkka.log; the terminal gets only
        // warnings and errors, in plain words, unless --debug.
        log.WriteTo.File(OrkkaInstance.LogFile(home),
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
            fileSizeLimitBytes: 20 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: 5,
            shared: false);
        if (debug)
        {
            log.MinimumLevel.Debug();
            log.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }
        else
        {
            log.WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning,
                outputTemplate: "{Timestamp:HH:mm:ss} {Message:lj}{NewLine}");
        }
    }

    private static async Task BootstrapAsync(LocalHostClient client, string home, CancellationToken ct)
    {
        var workspaces = await client.BootstrapAsync(ct);
        var outcome = RunnerProfileSync.Apply(home, client.BaseUrl, workspaces);
        foreach (var name in outcome.Added)
            Log.Information("Runner profile created for workspace '{Workspace}'", name);
        foreach (var name in outcome.MissingToken)
            Console.WriteLine($"Workspace '{name}' has no runner token on this machine — the runner cannot serve it until the host issues a new one.");
    }

    /// <summary>A one-time sign-in link: opened in the browser unless asked not to, printed always.</summary>
    internal static async Task<bool> OpenBrowserAsync(LocalHostClient client, bool openBrowser, CancellationToken ct)
    {
        try
        {
            var code = await client.CreateLaunchCodeAsync(ct);
            var url = LocalHostClient.LaunchUrl(client.HostPort, code);
            if (openBrowser && Browser.TryOpen(url))
                Console.WriteLine($"Opened Orkka in your browser. If it did not open, use this link (it works once, for a minute):");
            else
                Console.WriteLine("Sign this browser in with this link (it works once, for a minute):");
            Console.WriteLine($"  {url}");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Could not create a sign-in link: {ex.Message}. Run `./orkka open` to try again.");
            return false;
        }
    }

    internal static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
