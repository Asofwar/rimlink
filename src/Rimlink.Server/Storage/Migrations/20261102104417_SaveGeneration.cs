using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SaveGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SaveGeneration",
                table: "players",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SaveGeneration",
                table: "archived_colonies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SaveGeneration",
                table: "players");

            migrationBuilder.DropColumn(
                name: "SaveGeneration",
                table: "archived_colonies");
        }
    }
}
