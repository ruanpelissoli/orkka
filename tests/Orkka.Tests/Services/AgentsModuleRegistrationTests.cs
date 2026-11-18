namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orkka.Modules.Agents;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Which extension method a service is registered in is a real contract: the hosts call
/// different subsets, and a minimal-API endpoint parameter whose service is missing does not
/// fail loudly — ASP.NET reads it from the request BODY instead. That is exactly what took
/// every relayed chat turn down on 2026-09-08 with "Deserialization of interface or abstract
/// types is not supported", because the chat tool-server factory sat in the API-only method
/// while the WORKER's relay is what resolves it.
/// </summary>
public sealed class AgentsModuleRegistrationTests
{
    [Fact]
    public void AddAgentsModule_registers_the_chat_tool_server_factory_for_every_host()
    {
        // AddAgentsModule is the one method both the API host and the Worker call.
        var services = new ServiceCollection();
        services.AddAgentsModule(new ConfigurationBuilder().Build());

        services.Should().Contain(d => d.ServiceType == typeof(IChatToolServerFactory),
            "the Worker's chat relay resolves it, and the Worker never calls AddAgentsModuleApi");
    }
}
