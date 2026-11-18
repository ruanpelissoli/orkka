namespace Orkka.Cli;

using System.Text.Json;
using Orkka.LocalRunner.Cli;
using Orkka.LocalRunner.Runtime;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// <c>open</c>, <c>status</c> and <c>stop</c>: one-shot commands that talk to the running
/// <c>./orkka start</c> over 127.0.0.1 with the install secret (from the OS secret store) as the
/// bearer. Which port it is comes from <c>~/.orkka/run/instance.json</c>.
/// </summary>
public static class InstanceCommands
{
    public const string PrintFlag = "--print";

    private const string NotRunning = "Orkka is not running. Start it with `./orkka start`.";

    /// <summary><c>./orkka open [--print]</c> — a fresh one-time sign-in link, opened in the
    /// browser (or only printed with <c>--print</c>).</summary>
    public static async Task<int> OpenAsync(IReadOnlyList<string> args)
    {
        var print = args.Contains(PrintFlag, StringComparer.OrdinalIgnoreCase);
        if (Connect(out var info) is not { } client)
            return 1;
        using (client)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (!await client.IsHealthyAsync(cts.Token))
            {
                Console.Error.WriteLine($"Orkka on {info!.Url} is not answering yet — try again in a moment, or see `./orkka status`.");
                return 1;
            }
            if (print)
            {
                try
                {
                    // Only the URL, so a script can follow it.
                    Console.WriteLine(LocalHostClient.LaunchUrl(client.HostPort, await client.CreateLaunchCodeAsync(cts.Token)));
                    return 0;
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
                {
                    Console.Error.WriteLine($"Could not create a sign-in link: {ex.Message}");
                    return 1;
                }
            }
            return await StartCommand.OpenBrowserAsync(client, openBrowser: true, cts.Token) ? 0 : 1;
        }
    }

    /// <summary><c>./orkka status</c> — where Orkka runs and whether it is healthy. Exit 0 when
    /// healthy, 1 when not answering, 3 when not running.</summary>
    public static async Task<int> StatusAsync()
    {
        var home = RunnerEnvFile.OrkkaDir;
        if (OrkkaInstance.ReadAlive(home) is not { } info)
        {
            Console.WriteLine(NotRunning);
            return 3;
        }

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri(info.Url + "/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        string health;
        var healthy = false;
        try
        {
            using var response = await http.GetAsync("health");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = doc.RootElement.GetProperty("status").GetString() ?? "?";
            var checks = doc.RootElement.TryGetProperty("checks", out var list)
                ? string.Join(", ", list.EnumerateArray().Select(c => $"{c.GetProperty("name").GetString()}: {c.GetProperty("status").GetString()}"))
                : "";
            health = checks.Length > 0 ? $"{status} ({checks})" : status;
            healthy = response.IsSuccessStatusCode && status == "Healthy";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            health = "not answering";
        }

        Console.WriteLine($"Orkka is running at {info.Url}");
        Console.WriteLine($"  pid      {info.Pid}, up since {info.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        Console.WriteLine($"  health   {health}");
        Console.WriteLine($"  data     {Path.Combine(home, "data")}");
        Console.WriteLine($"  logs     {OrkkaInstance.LogFile(home)}");
        Console.WriteLine("`./orkka open` signs a browser in; `./orkka stop` stops it.");
        return healthy ? 0 : 1;
    }

    /// <summary><c>./orkka stop</c> — asks the running Orkka to shut down (the runner drains,
    /// then the host stops) and waits for its process to end. A process that does not answer or
    /// end in time is stopped, but only when its PID is still the one Orkka recorded.</summary>
    public static async Task<int> StopAsync()
    {
        var home = RunnerEnvFile.OrkkaDir;
        if (OrkkaInstance.ReadAlive(home) is not { } info)
        {
            OrkkaInstance.Delete(home);
            Console.WriteLine("Orkka is not running.");
            return 0;
        }
        using var process = OrkkaInstance.FindProcess(info);
        if (process is null)
        {
            Console.WriteLine("Orkka is not running.");
            return 0;
        }

        Console.WriteLine($"Stopping Orkka (pid {info.Pid})...");
        var asked = false;
        var secret = SecretOrNull(home);
        if (secret is not null)
        {
            using var client = new LocalHostClient(info.Port, secret);
            asked = await client.ShutdownAsync(CancellationToken.None);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(asked ? 60 : 1));
        try
        {
            await process.WaitForExitAsync(cts.Token);
            Console.WriteLine("Orkka stopped.");
            return 0;
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine(asked
            ? "Orkka did not stop in time — ending its process."
            : "Orkka did not answer — ending its process.");
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            OrkkaInstance.Delete(home);
            Console.WriteLine("Orkka stopped.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Could not stop pid {info.Pid}: {ex.Message}");
            return 1;
        }
    }

    private static LocalHostClient? Connect(out InstanceInfo? info)
    {
        var home = RunnerEnvFile.OrkkaDir;
        info = OrkkaInstance.ReadAlive(home);
        if (info is null)
        {
            Console.Error.WriteLine(NotRunning);
            return null;
        }
        var secret = SecretOrNull(home);
        if (secret is null)
        {
            Console.Error.WriteLine("This machine's Orkka install secret is missing from the OS secret store — run `./orkka stop`, then `./orkka start`.");
            return null;
        }
        return new LocalHostClient(info.Port, secret);
    }

    private static string? SecretOrNull(string home)
    {
        WorkspaceSecretStores.CaptureOverride();
        try
        {
            return LocalSecrets.Open(home).InstallSecret;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
