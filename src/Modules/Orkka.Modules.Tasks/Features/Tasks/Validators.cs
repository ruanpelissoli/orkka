namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Tasks.Features.Tasks;
using FastEndpoints;
using FluentValidation;

public sealed class CreateTaskValidator : Validator<CreateTaskRequest>
{
    public CreateTaskValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(500).WithMessage("Title must be 500 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(10000).WithMessage("Description must be 10,000 characters or fewer.");

        RuleFor(x => x.RepoId)
            .NotEmpty().WithMessage("RepoId is required.");

        RuleFor(x => x.Priority)
            .Must(p => Enum.TryParse<Contracts.Priority>(p, ignoreCase: true, out _))
            .WithMessage("Priority must be Low, Medium, High, or Critical.");

        RuleFor(x => x.Type)
            .Must(t => Enum.TryParse<Contracts.TaskType>(t, ignoreCase: true, out _))
            .WithMessage("Type must be Feature, BugFix, Refactor, or Improvement.");

        RuleFor(x => x.Model)
            .MaximumLength(100).WithMessage("Model must be 100 characters or fewer.")
            .When(x => x.Model is not null);
    }
}

public sealed class UpdateTaskValidator : Validator<UpdateTaskRequest>
{
    public UpdateTaskValidator()
    {
        RuleFor(x => x.Title)
            .MaximumLength(500).WithMessage("Title must be 500 characters or fewer.")
            .When(x => x.Title is not null);

        RuleFor(x => x.Description)
            .MaximumLength(10000).WithMessage("Description must be 10,000 characters or fewer.")
            .When(x => x.Description is not null);

        RuleFor(x => x.Priority)
            .Must(p => Enum.TryParse<Contracts.Priority>(p, ignoreCase: true, out _))
            .WithMessage("Priority must be Low, Medium, High, or Critical.")
            .When(x => x.Priority is not null);

        RuleFor(x => x.Type)
            .Must(t => Enum.TryParse<Contracts.TaskType>(t, ignoreCase: true, out _))
            .WithMessage("Type must be Feature, BugFix, Refactor, or Improvement.")
            .When(x => x.Type is not null);
    }
}

public sealed class MoveTaskLaneValidator : Validator<MoveTaskLaneRequest>
{
    public MoveTaskLaneValidator()
    {
        RuleFor(x => x.TargetLane)
            .NotEmpty().WithMessage("TargetLane is required.");
    }
}