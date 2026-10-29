using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SafeTrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AcceptGifts",
                table: "players",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TradesClosed",
                table: "players",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Decision",
                table: "parcels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "player_blocks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BlockerPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    BlockedPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_blocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "trades",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InitiatorPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    PartnerPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CloseReason = table.Column<string>(type: "TEXT", nullable: true),
                    ClientRequestId = table.Column<string>(type: "TEXT", nullable: true),
                    InitiatorItems = table.Column<byte[]>(type: "BLOB", nullable: false),
                    InitiatorPawns = table.Column<byte[]>(type: "BLOB", nullable: false),
                    InitiatorSilver = table.Column<long>(type: "INTEGER", nullable: false),
                    InitiatorNote = table.Column<string>(type: "TEXT", nullable: false),
                    InitiatorConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PartnerItems = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PartnerPawns = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PartnerSilver = table.Column<long>(type: "INTEGER", nullable: false),
                    PartnerNote = table.Column<string>(type: "TEXT", nullable: false),
                    PartnerConfirmed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trades", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_player_blocks_BlockedPlayerId",
                table: "player_blocks",
                column: "BlockedPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_player_blocks_BlockerPlayerId_BlockedPlayerId",
                table: "player_blocks",
                columns: new[] { "BlockerPlayerId", "BlockedPlayerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_trades_ClientRequestId",
                table: "trades",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_trades_InitiatorPlayerId",
                table: "trades",
                column: "InitiatorPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_trades_PartnerPlayerId",
                table: "trades",
                column: "PartnerPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_trades_Status",
                table: "trades",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_blocks");

            migrationBuilder.DropTable(
                name: "trades");

            migrationBuilder.DropColumn(
                name: "AcceptGifts",
                table: "players");

            migrationBuilder.DropColumn(
                name: "TradesClosed",
                table: "players");

            migrationBuilder.DropColumn(
                name: "Decision",
                table: "parcels");
        }
    }
}
