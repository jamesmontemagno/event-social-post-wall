using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWall.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlockedAuthors",
                columns: table => new
                {
                    EventSlug = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    AuthorExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StaffSessionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedAuthors", x => new { x.EventSlug, x.Source, x.AuthorExternalId });
                });

            migrationBuilder.CreateTable(
                name: "ConnectorCheckpoints",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Cursor = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorCheckpoints", x => x.Provider);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorDailyUsage",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReservedPostReads = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorDailyUsage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Slug = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SubmissionsOpen = table.Column<bool>(type: "bit", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    PostFiltersJson = table.Column<string>(type: ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "nvarchar(max)", maxLength: 14000, nullable: false, defaultValue: "{}"),
                    FilterSettingsVersion = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Slug);
                });

            migrationBuilder.CreateTable(
                name: "ModerationActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ToStatus = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StaffSessionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventSlug = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AuthorName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    AuthorExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AuthorProfileUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    AuthorAvatarUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ImagesJson = table.Column<string>(type: ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "nvarchar(max)", maxLength: 14000, nullable: false, defaultValue: "[]"),
                    SourceUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SourceCreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    PublishedRevision = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    FilterReasonsJson = table.Column<string>(type: ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "nvarchar(500)", maxLength: 500, nullable: false, defaultValue: "[]")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderCredentials",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ProtectedRefreshToken = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderCredentials", x => x.Provider);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationActions_PostId_CreatedAtUtc",
                table: "ModerationActions",
                columns: new[] { "PostId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_EventSlug_Source_ExternalId",
                table: "Posts",
                columns: new[] { "EventSlug", "Source", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_EventSlug_Status_PublishedRevision",
                table: "Posts",
                columns: new[] { "EventSlug", "Status", "PublishedRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_EventSlug_Status_ReceivedAtUtc",
                table: "Posts",
                columns: new[] { "EventSlug", "Status", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedAuthors");

            migrationBuilder.DropTable(
                name: "ConnectorCheckpoints");

            migrationBuilder.DropTable(
                name: "ConnectorDailyUsage");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "ModerationActions");

            migrationBuilder.DropTable(
                name: "Posts");

            migrationBuilder.DropTable(
                name: "ProviderCredentials");
        }
    }
}
