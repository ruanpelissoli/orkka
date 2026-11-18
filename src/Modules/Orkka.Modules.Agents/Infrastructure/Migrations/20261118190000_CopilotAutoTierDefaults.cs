using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orkka.Modules.Agents.Infrastructure;

#nullable disable

namespace Orkka.Modules.Agents.Infrastructure.Migrations
{
    /// <summary>
    /// Data-only: moves the Copilot Utility/Capable tier defaults onto "copilot/auto"
    /// (ProviderModelDefaults, 2026-11-18). AiModelSeeder only inserts missing rows, so an
    /// existing database would otherwise keep seeding new Copilot agents on
    /// copilot/claude-sonnet-5. Only rows still holding that shipped value change; nothing
    /// edits tier defaults in the local product, and agents' saved models are not touched.
    /// No model change, so no Designer/snapshot update.
    /// </summary>
    [DbContext(typeof(AgentsDbContext))]
    [Migration("20261118190000_CopilotAutoTierDefaults")]
    public partial class CopilotAutoTierDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE ai_tier_defaults SET ModelId = 'copilot/auto' " +
                "WHERE Provider = 'GitHubCopilot' AND Tier IN ('Utility', 'Capable') " +
                "AND ModelId = 'copilot/claude-sonnet-5';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE ai_tier_defaults SET ModelId = 'copilot/claude-sonnet-5' " +
                "WHERE Provider = 'GitHubCopilot' AND Tier IN ('Utility', 'Capable') " +
                "AND ModelId = 'copilot/auto';");
        }
    }
}
