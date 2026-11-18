namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// Ollama invoker plumbing: catalog-namespace stripping, the codex `-c` overrides that
/// point the CLI at the local Ollama server, and base-URL resolution from the
/// environment conventions (ORKKA_OLLAMA_URL &gt; OLLAMA_HOST &gt; default). The JSONL
/// event mapping is inherited from CodexAgentInvoker and covered by its parser tests.
/// </summary>
public class OllamaAgentInvokerTests
{
    // ── model namespace ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("ollama/gpt-oss:20b", "gpt-oss:20b")]
    [InlineData("OLLAMA/QWEN3-CODER", "QWEN3-CODER")]
    [InlineData("qwen3-coder:30b", "qwen3-coder:30b")] // un-namespaced passes through
    public void ToCliModel_strips_the_catalog_namespace(string catalogId, string expected)
        => OllamaAgentInvoker.ToCliModel(catalogId).Should().Be(expected);

    [Theory]
    [InlineData("ollama")]
    [InlineData("ollama/")]
    [InlineData("   ")]
    public void ToCliModel_defaults_when_no_bare_model_remains(string catalogId)
        => OllamaAgentInvoker.ToCliModel(catalogId).Should().Be("gpt-oss:20b");

    // ── codex provider overrides ─────────────────────────────────────────────

    [Fact]
    public void ProviderConfigArgs_point_codex_at_the_ollama_server()
    {
        var args = OllamaAgentInvoker.BuildProviderConfigArgs("http://localhost:11434");

        args.Should().Equal(
            "-c", "model_provider=ollama",
            "-c", "model_providers.ollama.name=Ollama",
            "-c", "model_providers.ollama.base_url=http://localhost:11434/v1",
            "-c", "model_providers.ollama.wire_api=chat");
    }

    [Fact]
    public void ProviderConfigArgs_normalize_a_trailing_slash()
        => OllamaAgentInvoker.BuildProviderConfigArgs("http://box:11434/")
            .Should().Contain("model_providers.ollama.base_url=http://box:11434/v1");

    // ── base-URL resolution ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_defaults_to_localhost()
        => OllamaEnvironment.Resolve(null, null)
            .Should().Be("http://localhost:11434");

    [Fact]
    public void Resolve_prefers_the_explicit_orkka_url()
        => OllamaEnvironment.Resolve("http://gpu-box:9999/", "other:1234")
            .Should().Be("http://gpu-box:9999");

    [Theory]
    [InlineData("127.0.0.1", "http://127.0.0.1:11434")] // bare host → ollama's default port
    [InlineData("gpu-box:8080", "http://gpu-box:8080")]
    [InlineData("http://gpu-box", "http://gpu-box:11434")]
    [InlineData("https://remote.example.com:443", "https://remote.example.com:443")]
    public void Resolve_normalizes_ollama_host_forms(string ollamaHost, string expected)
        => OllamaEnvironment.Resolve(null, ollamaHost).Should().Be(expected);
}
