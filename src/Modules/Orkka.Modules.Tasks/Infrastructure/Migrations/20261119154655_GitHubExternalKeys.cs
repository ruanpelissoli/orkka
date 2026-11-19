using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Tasks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GitHubExternalKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ExternalKey widened to 160 for GitHub keys (owner/repo#n). SQLite stores TEXT without
            // a length, so nothing changes on disk; the migration keeps the model snapshot current.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
