namespace Orkka.SharedKernel.Persistence;

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public sealed class NullableUlidToGuidConverter() : ValueConverter<Ulid?, Guid?>(
    ulid => ulid.HasValue ? ulid.Value.ToGuid() : null,
    guid => guid.HasValue ? new Ulid(guid.Value) : null);
