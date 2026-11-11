using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWall.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddYouTubeCollectionControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "YouTubeCollectionRunning",
                table: "Events",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                // Native DROP avoids rebuilding Events from SQL Server's nvarchar(max) snapshot.
                migrationBuilder.Sql("""ALTER TABLE "Events" DROP COLUMN "YouTubeCollectionRunning";""");
                return;
            }
            migrationBuilder.DropColumn(
                name: "YouTubeCollectionRunning",
                table: "Events");
        }
    }
}
