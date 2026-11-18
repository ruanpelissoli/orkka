namespace Orkka.SharedKernel.Utilities;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Ulid as its canonical 26-character string. Without this a Ulid serializes as its raw byte
/// fields, which is both unreadable and not what the HTTP contract promises.
/// </summary>
/// <remarks>
/// Lives in SharedKernel because two independent serializers need it: the FastEndpoints
/// serializer in each host, and the cache serializer (see <c>Caching/OrkkaJsonSerializerFactory</c>).
/// A cached payload and an HTTP payload must round-trip identically.
/// </remarks>
public sealed class UlidJsonConverter : JsonConverter<Ulid>
{
    public override Ulid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Ulid.Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, Ulid value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
