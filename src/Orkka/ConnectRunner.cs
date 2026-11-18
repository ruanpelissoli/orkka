namespace Orkka.Cli;

using Microsoft.Extensions.Hosting;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Cli;

/// <summary>
/// <c>./orkka connect</c> — advanced: the runner alone, in the foreground, against an Orkka host
/// elsewhere (another machine reachable to you, or another port). <c>./orkka start</c> already
/// runs the runner for this machine's Orkka, so most people never need this.
/// </summary>
public static class ConnectRunner
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, bool debug)
    {
        ConsoleBanner.Print();
        var profiles = ConnectCommand.Resolve(args, RunnerEnvFile.OrkkaDir, out var error, out var notice);
        if (notice is not null)
            Console.WriteLine(notice);
        if (profiles is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        if (profiles.Count == 0)
            Console.WriteLine("No workspace yet — waiting for the first one...");
        foreach (var profile in profiles)
            Console.WriteLine(profiles.Count == 1
                ? $"Connecting to {profile.HostUrl} ..."
                : $"[{profile.Name}] connecting to {profile.HostUrl} ...");

        var machine = RunnerProcess.PrepareEnvironment(Console.WriteLine);
        using var host = RunnerHost.Build(new RunnerHostOptions
        {
            Machine = machine,
            Profiles = profiles,
            Debug = debug,
        });
        await host.RunAsync();
        return 0;
    }
}
