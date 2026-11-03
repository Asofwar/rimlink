using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ModpackDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "modpack_packages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UploadExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Creator = table.Column<string>(type: "TEXT", nullable: false),
                    RedistributionConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublicDistributionConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    SharedSettingsConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Verified = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_modpack_packages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "modpack_publication",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    PackageId = table.Column<string>(type: "TEXT", nullable: true),
                    PreviousPackageId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_modpack_publication", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modpack_packages");

            migrationBuilder.DropTable(
                name: "modpack_publication");
        }
    }
}
