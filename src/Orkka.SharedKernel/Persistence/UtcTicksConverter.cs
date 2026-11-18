namespace Orkka.SharedKernel.Persistence;

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as its UTC ticks (an INTEGER column) and reads it back at
/// offset zero. SQLite has no date type: as text an offset breaks ordering and EF cannot translate
/// ORDER BY or comparisons over it; as ticks both are plain integer operations and stay correct
/// whatever offset the value was written with.
/// </summary>
public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks,
    ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
