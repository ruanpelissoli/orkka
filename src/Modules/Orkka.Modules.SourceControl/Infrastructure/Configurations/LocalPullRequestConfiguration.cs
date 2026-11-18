namespace Orkka.Modules.SourceControl.Infrastructure.Configurations;

using Orkka.Modules.SourceControl.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class LocalPullRequestConfiguration : IEntityTypeConfiguration<LocalPullRequest>
{
    public void Configure(EntityTypeBuilder<LocalPullRequest> builder)
    {
        builder.ToTable("local_pull_requests");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(500).IsRequired();
        builder.Property(x => x.HeadBranch).HasMaxLength(400).IsRequired();
        builder.Property(x => x.BaseBranch).HasMaxLength(400).IsRequired();
        builder.Property(x => x.ForgeUrl).HasMaxLength(2048);
        builder.Property(x => x.ForgeState).HasMaxLength(20);

        // Numbers are handed out per org and every forge-shaped caller looks a pull request
        // up by number alone, so this pair is the real identity — and the uniqueness that
        // stops two concurrent sweeps from minting the same number.
        builder.HasIndex(x => new { x.OrgId, x.Number }).IsUnique();

        // The idempotency lookup on create ("is there already an open PR for this branch?")
        // and the merge sweep's per-repo scan.
        builder.HasIndex(x => new { x.OrgId, x.RepoId, x.HeadBranch });
    }
}

public sealed class LocalPrReviewConfiguration : IEntityTypeConfiguration<LocalPrReview>
{
    public void Configure(EntityTypeBuilder<LocalPrReview> builder)
    {
        builder.ToTable("local_pr_reviews");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.State).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Author).HasMaxLength(200).IsRequired();
        builder.HasIndex(x => x.PullRequestId);
    }
}

public sealed class LocalPrCommentConfiguration : IEntityTypeConfiguration<LocalPrComment>
{
    public void Configure(EntityTypeBuilder<LocalPrComment> builder)
    {
        builder.ToTable("local_pr_comments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Path).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Author).HasMaxLength(200).IsRequired();
        builder.HasIndex(x => x.PullRequestId);
    }
}
