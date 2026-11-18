namespace Orkka.Modules.Tasks.Domain;

public sealed class PlanContent
{
    public required string Summary { get; set; }
    public required string Approach { get; set; }
    public List<PlannedFileChange>? FilesChanged { get; set; }
    public int? EstimatedTokens { get; set; }
}

public sealed class PlannedFileChange
{
    public required string Path { get; set; }
    public required string Action { get; set; }
    public required string Description { get; set; }
}
