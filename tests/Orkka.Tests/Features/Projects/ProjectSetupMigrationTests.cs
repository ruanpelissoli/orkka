namespace Orkka.Tests.Features.Projects;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.SharedKernel.Persistence;

/// <summary>
/// The guided setup holds a NEW project's ticket source back until Start. Every project that
/// existed before it was already being polled, so the migration that adds the gate marks each of
/// them started — at its creation time, with every step confirmed — and nothing changes for them.
/// </summary>
public sealed class ProjectSetupMigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"orkka-setup-migration-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { /* temp — best effort */ }
    }

    private ProjectsDbContext Context()
    {
        var options = new DbContextOptionsBuilder<ProjectsDbContext>();
        options.UseOrkkaSqlite<ProjectsDbContext>(SqliteDatabase.ConnectionStringFor(_path), ProjectsDbContext.Module);
        return new ProjectsDbContext(options.Options);
    }

    [Fact]
    public async Task ProjectsThatExistBeforeTheMigration_AreStarted_AtTheirCreation_WithEveryStepConfirmed()
    {
        var id = Ulid.NewUlid().ToGuid().ToString();
        var createdAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await using (var db = Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261118141900_Initial");
            await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "projects" ("Id","Name","Slug","Key","LastTaskNumber","CreatedAt","UpdatedAt","OrgId") VALUES ({0},'Shop','shop','SHOP',0,{1},{1},{2})""",
                id, createdAt.UtcTicks, Ulid.NewUlid().ToGuid().ToString());
        }

        await using (var db = Context())
        {
            await db.Database.MigrateAsync();
            var project = await db.Projects.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            project.SetupCompletedAt.Should().Be(createdAt);
            project.SetupStepsJson.Should().Be("""["folder","machine","source","team"]""");

            // A project created after the migration starts with its setup open.
            db.Projects.Add(new Orkka.Modules.Projects.Domain.Project
            {
                OrgId = Ulid.NewUlid(), Name = "New", Slug = "new", Key = "NEW",
            });
            await db.SaveChangesAsync();
            var fresh = await db.Projects.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Slug == "new");
            fresh.SetupCompletedAt.Should().BeNull();
            fresh.SetupStepsJson.Should().Be("[]");
        }
    }
}
