using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Raids : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSaveAt",
                table: "players",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Peaceful",
                table: "players",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PeacefulOffAt",
                table: "players",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VisitsClosed",
                table: "players",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "WealthItems",
                table: "players",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "WealthTotal",
                table: "players",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "pending_removals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HostPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    ThingId = table.Column<long>(type: "INTEGER", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AckedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_removals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "raid_outcomes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RaidId = table.Column<long>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raid_outcomes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "raids",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AttackerPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    PawnMode = table.Column<int>(type: "INTEGER", nullable: false),
                    Timing = table.Column<int>(type: "INTEGER", nullable: false),
                    Arrival = table.Column<int>(type: "INTEGER", nullable: false),
                    Controller = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Pawns = table.Column<byte[]>(type: "BLOB", nullable: false),
                    LootCap = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AnnouncedTick = table.Column<long>(type: "INTEGER", nullable: true),
                    LandAtTick = table.Column<long>(type: "INTEGER", nullable: true),
                    LandedTick = table.Column<long>(type: "INTEGER", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SummaryJson = table.Column<string>(type: "TEXT", nullable: true),
                    ClientRequestId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raids", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shop_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HostPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    ThingId = table.Column<long>(type: "INTEGER", nullable: false),
                    DefName = table.Column<string>(type: "TEXT", nullable: false),
                    StuffDef = table.Column<string>(type: "TEXT", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false),
                    HitPoints = table.Column<int>(type: "INTEGER", nullable: false),
                    Price = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveTick = table.Column<long>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    SoldAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shop_items", x => x.Id);
                    table.CheckConstraint("CK_shop_items_count", "\"Count\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "visits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VisitorPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    HostPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_removals_HostPlayerId_AckedAt",
                table: "pending_removals",
                columns: new[] { "HostPlayerId", "AckedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_raid_outcomes_RaidId",
                table: "raid_outcomes",
                column: "RaidId");

            migrationBuilder.CreateIndex(
                name: "IX_raids_AttackerPlayerId_ClientRequestId",
                table: "raids",
                columns: new[] { "AttackerPlayerId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_raids_AttackerPlayerId_State",
                table: "raids",
                columns: new[] { "AttackerPlayerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_raids_TargetPlayerId_State",
                table: "raids",
                columns: new[] { "TargetPlayerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_shop_items_HostPlayerId_ThingId",
                table: "shop_items",
                columns: new[] { "HostPlayerId", "ThingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_visits_VisitorPlayerId_EndedAt",
                table: "visits",
                columns: new[] { "VisitorPlayerId", "EndedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_removals");

            migrationBuilder.DropTable(
                name: "raid_outcomes");

            migrationBuilder.DropTable(
                name: "raids");

            migrationBuilder.DropTable(
                name: "shop_items");

            migrationBuilder.DropTable(
                name: "visits");

            migrationBuilder.DropColumn(
                name: "FirstSaveAt",
                table: "players");

            migrationBuilder.DropColumn(
                name: "Peaceful",
                table: "players");

            migrationBuilder.DropColumn(
                name: "PeacefulOffAt",
                table: "players");

            migrationBuilder.DropColumn(
                name: "VisitsClosed",
                table: "players");

            migrationBuilder.DropColumn(
                name: "WealthItems",
                table: "players");

            migrationBuilder.DropColumn(
                name: "WealthTotal",
                table: "players");
        }
    }
}
