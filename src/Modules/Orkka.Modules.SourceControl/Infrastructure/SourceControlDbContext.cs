namespace Orkka.Modules.SourceControl.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.SourceControl.Domain;
using Orkka.SharedKernel.Persistence;
using Orkka.SharedKernel.Security;

public sealed class SourceControlDbContext(
    DbContextOptions<SourceControlDbContext> options,
    ICurrentUser? currentUser = null) : ModuleDbContext(options, currentUser)
{
    public const string Module = "sourcecontrol";
    public override string ModuleName => Module;

    // Orkka's own pull requests, for every repository — see LocalPullRequest and
    // IPullRequestService, which this module owns.
    public DbSet<LocalPullRequest> LocalPullRequests => Set<LocalPullRequest>();
    public DbSet<LocalPrReview> LocalPrReviews => Set<LocalPrReview>();
    public DbSet<LocalPrComment> LocalPrComments => Set<LocalPrComment>();

    /// <summary>The git engine's operation ledger — platform-wide, never tenant-filtered.</summary>
    public DbSet<GitOperation> GitOperations => Set<GitOperation>();
}
