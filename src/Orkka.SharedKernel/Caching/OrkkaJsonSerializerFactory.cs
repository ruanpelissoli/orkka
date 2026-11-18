namespace Orkka.SharedKernel.Caching;

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Hybrid;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// JSON serialization for cached payloads, using Orkka's own converters rather than the
/// framework defaults.
/// </summary>
/// <remarks>
/// Two defaults would silently corrupt data here:
/// <list type="bullet">
/// <item><c>Ulid</c> has no built-in converter, so it round-trips as its raw byte fields —
/// nearly every cached DTO in this codebase is full of them.</item>
/// <item>Enums serialize as <b>numbers</b>, which pins the cache format to declaration order.
/// Reordering <c>Permission</c> or <c>TaskStatus</c> would then reinterpret every entry
/// written by the previous deploy as a different value. Names are stable; ordinals are not.</item>
/// </list>
/// </remarks>
public sealed class OrkkaJsonSerializerFactory : IHybridCacheSerializerFactory
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new UlidJsonConverter(), new JsonStringEnumConverter() }
    };

    public bool TryCreateSerializer<T>(out IHybridCacheSerializer<T> serializer)
    {
        serializer = new OrkkaJsonSerializer<T>();
        return true;
    }
}

internal sealed class OrkkaJsonSerializer<T> : IHybridCacheSerializer<T>
{
    public T Deserialize(ReadOnlySequence<byte> source)
    {
        var reader = new Utf8JsonReader(source);
        return JsonSerializer.Deserialize<T>(ref reader, OrkkaJsonSerializerFactory.Options)!;
    }

    public void Serialize(T value, IBufferWriter<byte> target)
    {
        using var writer = new Utf8JsonWriter(target);
        JsonSerializer.Serialize(writer, value, OrkkaJsonSerializerFactory.Options);
    }
}
