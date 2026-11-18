namespace Orkka.Modules.Agents.Features.Agents;

using FastEndpoints;
using FluentValidation;

// Prompt caps match the runtime budgets in the task pipeline (TaskOrchestrator's
// PersonaCharCap = 12k / UserInstructionsCharCap = 4k): anything longer would be
// silently truncated out of the prompt at execution time, so reject it at the door
// instead of letting users write instructions that partially vanish.
public sealed class UpdateAgentValidator : Validator<UpdateAgentRequest>
{
    public UpdateAgentValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(100)
            .When(x => x.Name is not null);

        RuleFor(x => x.UserInstructions)
            .MaximumLength(4_000)
            .When(x => x.UserInstructions is not null)
            .WithMessage("Instructions must be 4,000 characters or fewer — longer text would be truncated at execution time.");
    }
}
