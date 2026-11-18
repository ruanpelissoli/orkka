namespace Orkka.Modules.Tasks.Contracts;

public sealed record StreamEvent(
    string Type,
    string Content,
    DateTimeOffset Timestamp,
    object? Metadata = null);
