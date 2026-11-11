using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWall.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddXUserReadAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReservedUserReads",
                table: "ConnectorDailyUsage",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReservedUserReads",
                table: "ConnectorDailyUsage");
        }
    }
}
