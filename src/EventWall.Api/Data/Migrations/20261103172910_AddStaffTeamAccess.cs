using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWall.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffTeamAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StaffMemberId",
                table: "ModerationActions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StaffUsername",
                table: "ModerationActions",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StaffMemberId",
                table: "BlockedAuthors",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StaffUsername",
                table: "BlockedAuthors",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StaffMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AccessCodeHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CodeRotatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSignedInAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffMembers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationActions_StaffMemberId",
                table: "ModerationActions",
                column: "StaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_NormalizedUsername",
                table: "StaffMembers",
                column: "NormalizedUsername",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffMembers");

            migrationBuilder.DropIndex(
                name: "IX_ModerationActions_StaffMemberId",
                table: "ModerationActions");

            migrationBuilder.DropColumn(
                name: "StaffMemberId",
                table: "ModerationActions");

            migrationBuilder.DropColumn(
                name: "StaffUsername",
                table: "ModerationActions");

            migrationBuilder.DropColumn(
                name: "StaffMemberId",
                table: "BlockedAuthors");

            migrationBuilder.DropColumn(
                name: "StaffUsername",
                table: "BlockedAuthors");
        }
    }
}
