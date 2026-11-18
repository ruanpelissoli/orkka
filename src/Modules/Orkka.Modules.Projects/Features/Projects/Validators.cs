namespace Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.SharedKernel.Utilities;
using FastEndpoints;
using FluentValidation;

public sealed class CreateProjectValidator : Validator<CreateProjectRequest>
{
    public CreateProjectValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Project name is required.")
            .MaximumLength(200).WithMessage("Project name must be 200 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Description must be 2000 characters or fewer.")
            .When(x => x.Description is not null);

        RuleFor(x => x.Slug!)
            .Matches("^[a-z0-9]+(-[a-z0-9]+)*$").WithMessage("Slug must be lowercase letters, digits, and dashes.")
            .MaximumLength(100).WithMessage("Slug must be 100 characters or fewer.")
            .Must(s => !SlugGenerator.ReservedProjectSlugs.Contains(s)).WithMessage("This slug is reserved.")
            .When(x => x.Slug is not null);

        RuleFor(x => x.Key!)
            .Matches("^[A-Z][A-Z0-9]{1,4}$").WithMessage("Key must be 2-5 characters: uppercase letters and digits, starting with a letter.")
            .When(x => x.Key is not null);
    }
}

public sealed class UpdateProjectValidator : Validator<UpdateProjectRequest>
{
    public UpdateProjectValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Project name cannot be empty.")
            .MaximumLength(200).WithMessage("Project name must be 200 characters or fewer.")
            .When(x => x.Name is not null);

        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Description must be 2000 characters or fewer.")
            .When(x => x.Description is not null);

        RuleFor(x => x.Slug!)
            .Matches("^[a-z0-9]+(-[a-z0-9]+)*$").WithMessage("Slug must be lowercase letters, digits, and dashes.")
            .MaximumLength(100).WithMessage("Slug must be 100 characters or fewer.")
            .Must(s => !SlugGenerator.ReservedProjectSlugs.Contains(s)).WithMessage("This slug is reserved.")
            .When(x => x.Slug is not null);

        // Only checked when the limit is actually switched on: the minutes field keeps its
        // last value while the toggle is off, and rejecting a whole settings save over a
        // number nothing reads would be the wrong trade.
        RuleFor(x => x.Settings!.TaskRuntimeLimitMinutes)
            .InclusiveBetween(
                ProjectSettingsDto.MinTaskRuntimeLimitMinutes,
                ProjectSettingsDto.MaxTaskRuntimeLimitMinutes)
            .WithMessage(
                $"Task runtime limit must be between {ProjectSettingsDto.MinTaskRuntimeLimitMinutes} "
                + $"and {ProjectSettingsDto.MaxTaskRuntimeLimitMinutes} minutes.")
            .When(x => x.Settings is { TaskRuntimeLimitEnabled: true });

        RuleFor(x => x.Settings!.TaskSpendLimitUsd)
            .InclusiveBetween(
                ProjectSettingsDto.MinTaskSpendLimitUsd,
                ProjectSettingsDto.MaxTaskSpendLimitUsd)
            .WithMessage(
                $"Task spend limit (estimated list-price spend) must be between US${ProjectSettingsDto.MinTaskSpendLimitUsd:0.00} "
                + $"and US${ProjectSettingsDto.MaxTaskSpendLimitUsd:N0}.")
            .When(x => x.Settings is { TaskSpendLimitEnabled: true });
    }
}
