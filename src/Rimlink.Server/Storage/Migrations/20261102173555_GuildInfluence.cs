using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class GuildInfluence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InfluenceAllianceId",
                table: "world_objects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "InfluenceEnabled",
                table: "world_objects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "InfluenceRootSurface",
                table: "world_objects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InfluenceSaveGeneration",
                table: "world_objects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "InfluenceTile",
                table: "world_objects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "InfluenceVersion",
                table: "world_objects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "InfluenceWorldId",
                table: "world_objects",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "InfluenceInstanceId",
                table: "world",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "InfluenceRevision",
                table: "world",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "InfluenceAllianceId",
                table: "archived_colonies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "InfluenceEnabled",
                table: "archived_colonies",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "InfluenceRootSurface",
                table: "archived_colonies",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InfluenceSaveGeneration",
                table: "archived_colonies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "InfluenceSettlementId",
                table: "archived_colonies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "InfluenceTile",
                table: "archived_colonies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "InfluenceVersion",
                table: "archived_colonies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "InfluenceWorldId",
                table: "archived_colonies",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PaletteIndex",
                table: "alliance_groups",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "influence_operations",
                columns: table => new
                {
                    Actor = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Intent = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Result = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_influence_operations", x => new { x.Actor, x.RequestId });
                });

            migrationBuilder.Sql("UPDATE world SET InfluenceInstanceId = lower(hex(randomblob(16))) WHERE InfluenceInstanceId = ''; ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "influence_operations");

            migrationBuilder.DropColumn(
                name: "InfluenceAllianceId",
                table: "world_objects");

            migrationBuilder.DropColumn(
                name: "InfluenceEnabled",
                table: "world_objects");

            migrationBuilder.DropColumn(
                name: "InfluenceRootSurface",
                table: "world_objects");

            migrationBuilder.DropColumn(
                name: "InfluenceSaveGeneration",
                table: "world_objects");

            migrationBuilder.DropColumn(
                name: "InfluenceTile",
                table: "world_objects");

            migrationBuilder.DropColumn(
                name: "InfluenceVersion",
                table: "world_objects");

            migrationBuilder.DropColumn(
                name: "InfluenceWorldId",
                table: "world_objects");

            migrationBuilder.DropColumn(
                name: "InfluenceInstanceId",
                table: "world");

            migrationBuilder.DropColumn(
                name: "InfluenceRevision",
                table: "world");

            migrationBuilder.DropColumn(
                name: "InfluenceAllianceId",
                table: "archived_colonies");

            migrationBuilder.DropColumn(
                name: "InfluenceEnabled",
                table: "archived_colonies");

            migrationBuilder.DropColumn(
                name: "InfluenceRootSurface",
                table: "archived_colonies");

            migrationBuilder.DropColumn(
                name: "InfluenceSaveGeneration",
                table: "archived_colonies");

            migrationBuilder.DropColumn(
                name: "InfluenceSettlementId",
                table: "archived_colonies");

            migrationBuilder.DropColumn(
                name: "InfluenceTile",
                table: "archived_colonies");

            migrationBuilder.DropColumn(
                name: "InfluenceVersion",
                table: "archived_colonies");

            migrationBuilder.DropColumn(
                name: "InfluenceWorldId",
                table: "archived_colonies");

            migrationBuilder.DropColumn(
                name: "PaletteIndex",
                table: "alliance_groups");
        }
    }
}
