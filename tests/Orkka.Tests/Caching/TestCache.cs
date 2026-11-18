namespace Orkka.Tests.Caching;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orkka.SharedKernel.Caching;

/// <summary>
/// A real, isolated cache for tests.
/// </summary>
/// <remarks>
/// Built through the production registration path with no configuration, which yields the
/// in-process-only shape (see <see cref="CachingExtensions.AddOrkkaCache"/>). Tests therefore
/// exercise the same HybridCache wiring, serializer and key versioning the app runs on rather
/// than a stand-in that could diverge — and no Redis is involved, so it stays hermetic.
///
/// One instance per test class instance, which xunit creates per test, so entries never bleed
/// between tests.
/// </remarks>
internal sealed class TestCache : IDisposable
{
    private readonly ServiceProvider _provider;

    public TestCache()
    {
        _provider = new ServiceCollection()
            .AddOrkkaCache(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        Cache = _provider.GetRequiredService<IOrkkaCache>();
        Invalidator = _provider.GetRequiredService<ICacheInvalidator>();
    }

    public IOrkkaCache Cache { get; }

    public ICacheInvalidator Invalidator { get; }

    public void Dispose() => _provider.Dispose();
}
