namespace Orkka.SharedKernel.Persistence;

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public sealed class UlidToGuidConverter() : ValueConverter<Ulid, Guid>(
    ulid => ulid.ToGuid(),
    guid => new Ulid(guid));
