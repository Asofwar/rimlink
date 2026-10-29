using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ModpackFilesAndConfigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FilesHash",
                table: "modpack_entries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "modpack_configs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<string>(type: "TEXT", nullable: false),
                    Handle = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_modpack_configs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_modpack_configs_PackageId_Handle",
                table: "modpack_configs",
                columns: new[] { "PackageId", "Handle" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modpack_configs");

            migrationBuilder.DropColumn(
                name: "FilesHash",
                table: "modpack_entries");
        }
    }
}
