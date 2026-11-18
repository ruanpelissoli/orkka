namespace Orkka.IntegrationTests;

using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;

/// <summary>
/// The host and the runner share one process, and every agent the runner spawns inherits its
/// environment. So the host's secrets travel in memory (OrkkaHostOptions.Settings), never
/// through the process environment, where the agents' sanitizer would be the only thing
/// between them and an agent.
/// </summary>
[Collection("Integration")]
public sealed class HostSecretsTests(OrkkaApiFactory factory)
{
    [Fact]
    public void After_startup_no_host_secret_is_in_the_process_environment()
    {
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var secrets = new[]
        {
            configuration["Encryption:Key"],
            configuration[Orkka.Local.LocalSettings.InstallSecretKey],
            configuration[Orkka.Local.OrkkaHost.WorkerKeySetting],
        };
        secrets.Should().AllSatisfy(s => s.Should().NotBeNullOrEmpty());

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = entry.Key.ToString()!;
            var value = entry.Value?.ToString() ?? "";
            secrets.Should().NotContain(value, $"the environment variable {name} must not carry a host secret");
            new[] { "ORKKA_LOCAL_SECRET", "ORKKA_PROXY_SECRET", "Encryption__Key", "WorkerAuth__ApiKey", "Local__InstallSecret" }
                .Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).Should().BeFalse(name);
        }
    }
}
