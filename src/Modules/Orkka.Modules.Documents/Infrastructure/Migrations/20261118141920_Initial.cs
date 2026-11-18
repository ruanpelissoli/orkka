using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Documents.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    FileBlobName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FileContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CurrentVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SourceAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "document_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    ChangedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ChangedByAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequestedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SourceConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_revisions_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_revisions_DocumentId_Version",
                table: "document_revisions",
                columns: new[] { "DocumentId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_revisions_SourceTaskId",
                table: "document_revisions",
                column: "SourceTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_documents_OrgId_ProjectId",
                table: "documents",
                columns: new[] { "OrgId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_documents_SourceMessageId",
                table: "documents",
                column: "SourceMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_documents_SourceTaskId",
                table: "documents",
                column: "SourceTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_revisions");

            migrationBuilder.DropTable(
                name: "documents");
        }
    }
}
