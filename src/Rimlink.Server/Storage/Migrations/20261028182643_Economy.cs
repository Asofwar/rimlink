using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Economy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastOutgoingTick",
                table: "players",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "lots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SellerPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Items = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Pawn = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Price = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    BuyerPlayerId = table.Column<long>(type: "INTEGER", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClientRequestId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "modpack_defs",
                columns: table => new
                {
                    DefName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_modpack_defs", x => x.DefName);
                });

            migrationBuilder.CreateTable(
                name: "parcels",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ToPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    FromPlayerId = table.Column<long>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Items = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Pawns = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Silver = table.Column<long>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeliveredTick = table.Column<long>(type: "INTEGER", nullable: true),
                    ClientRequestId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parcels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "transfers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    ToPlayerId = table.Column<long>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    GameTick = table.Column<long>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wallet_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Delta = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RefId = table.Column<long>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Balance = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallets", x => x.PlayerId);
                    table.CheckConstraint("CK_wallets_balance", "\"Balance\" >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_lots_ClientRequestId",
                table: "lots",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_lots_SellerPlayerId",
                table: "lots",
                column: "SellerPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_lots_Status_ExpiresAt",
                table: "lots",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_parcels_ClientRequestId",
                table: "parcels",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_parcels_ToPlayerId_DeliveredAt",
                table: "parcels",
                columns: new[] { "ToPlayerId", "DeliveredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_FromPlayerId",
                table: "transfers",
                column: "FromPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_PlayerId",
                table: "wallet_entries",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lots");

            migrationBuilder.DropTable(
                name: "modpack_defs");

            migrationBuilder.DropTable(
                name: "parcels");

            migrationBuilder.DropTable(
                name: "transfers");

            migrationBuilder.DropTable(
                name: "wallet_entries");

            migrationBuilder.DropTable(
                name: "wallets");

            migrationBuilder.DropColumn(
                name: "LastOutgoingTick",
                table: "players");
        }
    }
}
