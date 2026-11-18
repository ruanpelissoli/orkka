namespace Orkka.SharedKernel.Caching;

using System.Diagnostics.Metrics;

/// <summary>
/// Hit/miss and avoided-work metrics, tagged by key <i>family</i>.
/// </summary>
/// <remarks>
/// <para>HybridCache publishes its own EventCounters, but they are process-wide totals — good
/// for the L1-vs-L2 split, useless for answering "is caching the project summary actually
/// worth it?". These are per family so each cached read can be justified or removed on
/// evidence rather than intuition.</para>
/// <para>The tag is the family, never the full key: a per-key tag would be unbounded
/// cardinality and would take the metrics backend down with it.</para>
/// </remarks>
public sealed class CacheMetrics
{
    public const string MeterName = "Orkka.Cache";

    // Owned directly rather than resolved from IMeterFactory so the type stays usable from a
    // bare ServiceCollection in unit tests. Nothing exports them: Orkka sends no telemetry.
    // They are readable on this machine only, e.g. `dotnet-counters monitor --counters Orkka.Cache`.
    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private readonly Counter<long> _requests;
    private readonly Histogram<double> _factoryDuration;
    private readonly Counter<long> _invalidations;

    public CacheMetrics()
    {
        var meter = Meter;

        _requests = meter.CreateCounter<long>(
            "orkka.cache.requests",
            unit: "{request}",
            description: "Cache lookups, split by hit and miss.");

        _factoryDuration = meter.CreateHistogram<double>(
            "orkka.cache.factory.duration",
            unit: "ms",
            description: "Time spent in the value factory on a miss — i.e. the work a hit avoids.");

        _invalidations = meter.CreateCounter<long>(
            "orkka.cache.invalidations",
            unit: "{key}",
            description: "Keys evicted, split by whether this node or a peer initiated it.");
    }

    public void RecordHit(string family)
        => _requests.Add(1, new KeyValuePair<string, object?>("family", family),
                            new KeyValuePair<string, object?>("result", "hit"));

    public void RecordMiss(string family, TimeSpan factoryDuration)
    {
        _requests.Add(1, new KeyValuePair<string, object?>("family", family),
                         new KeyValuePair<string, object?>("result", "miss"));
        _factoryDuration.Record(factoryDuration.TotalMilliseconds,
            new KeyValuePair<string, object?>("family", family));
    }

    /// <param name="source">"local" when this node initiated it, "backplane" when a peer did.</param>
    public void RecordInvalidation(string family, string source)
        => _invalidations.Add(1, new KeyValuePair<string, object?>("family", family),
                                 new KeyValuePair<string, object?>("source", source));
}

/// <summary>Derives the bounded metric tag from a cache key.</summary>
public static class CacheFamily
{
    /// <summary>
    /// First two colon-separated segments, dot-joined: <c>projects:summary:{org}:{id}</c> →
    /// <c>projects.summary</c>. Segments beyond the second are identifiers and must never
    /// reach a metric tag.
    /// </summary>
    public static string Of(string key)
    {
        var first = key.IndexOf(':');
        if (first < 0)
            return key;

        var second = key.IndexOf(':', first + 1);
        var end = second < 0 ? key.Length : second;

        return string.Concat(key.AsSpan(0, first), ".", key.AsSpan(first + 1, end - first - 1));
    }
}
