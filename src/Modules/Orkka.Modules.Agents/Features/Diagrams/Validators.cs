namespace Orkka.Modules.Agents.Features.Diagrams;

using System.Text.Json;
using FastEndpoints;
using FluentValidation;

public sealed class UpdateDiagramValidator : Validator<UpdateDiagramRequest>
{
    private const int MaxDiagramDataBytes = 5 * 1024 * 1024; // 5MB

    public UpdateDiagramValidator()
    {
        RuleFor(x => x.Title)
            .MaximumLength(500)
            .When(x => x.Title is not null);

        RuleFor(x => x.DiagramData)
            .Must(data => data is null || data.Value.ValueKind == JsonValueKind.Object)
            .WithMessage("Diagram data must be a JSON object.")
            .Must(data => data is null || System.Text.Encoding.UTF8.GetByteCount(data.Value.GetRawText()) <= MaxDiagramDataBytes)
            .WithMessage($"Diagram data must not exceed {MaxDiagramDataBytes / 1024 / 1024}MB.");

        RuleFor(x => x.Description)
            .MaximumLength(5000)
            .When(x => x.Description is not null);
    }
}
