namespace Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using FastEndpoints;
using FluentValidation;

public sealed class AddProjectRepoValidator : Validator<AddProjectRepoRequest>
{
    public AddProjectRepoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Repository name is required.")
            .MaximumLength(200).WithMessage("Repository name must be 200 characters or fewer.");

        RuleFor(x => x.GitUrl)
            .NotEmpty().WithMessage("Git URL is required.")
            .MaximumLength(2048).WithMessage("Git URL must be 2048 characters or fewer.")
            // Any remote the developer's own git can reach — https, ssh, scp-style, host
            // aliases from ~/.ssh/config. A folder on their machine is linked through their
            // runner instead.
            .Must(RepoUrl.IsRemote)
            .WithMessage("Enter the repository's clone URL — https (https://github.com/acme/api.git) "
                + "or ssh (git@github.com:acme/api.git).")
            // Credentials in an https URL would be stored here and written into clones.
            .Must(url => !RepoUrl.HasEmbeddedCredential(url))
            .WithMessage("Remove the credentials from the URL — your runner uses your own git sign-in.");

        RuleFor(x => x.DefaultBranch)
            .NotEmpty().WithMessage("Default branch is required.")
            .MaximumLength(100).WithMessage("Default branch must be 100 characters or fewer.");
    }
}

public sealed class UpdateProjectRepoValidator : Validator<UpdateProjectRepoRequest>
{
    public UpdateProjectRepoValidator()
    {
        RuleFor(x => x.DefaultBranch)
            .NotEmpty().WithMessage("Default branch is required.")
            .MaximumLength(100).WithMessage("Default branch must be 100 characters or fewer.");

        RuleFor(x => x.SetupCommand)
            .MaximumLength(2000).WithMessage("Setup command must be 2000 characters or fewer.");

        RuleFor(x => x.QaRunCommand)
            .MaximumLength(2000).WithMessage("Run command must be 2000 characters or fewer.");

        RuleFor(x => x.BuildCommand)
            .MaximumLength(2000).WithMessage("Build command must be 2000 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(300);

        RuleFor(x => x.CheckCommand)
            .MaximumLength(2000).WithMessage("Check command must be 2000 characters or fewer.");
    }
}
