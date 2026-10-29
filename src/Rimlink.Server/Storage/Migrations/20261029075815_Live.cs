using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Live : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoAcceptLive",
                table: "players",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "live_sessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    HostPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    GuestPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    RaidId = table.Column<long>(type: "INTEGER", nullable: true),
                    Pawns = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SnapshotVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndReason = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientRequestId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_live_sessions_GuestPlayerId_ClientRequestId",
                table: "live_sessions",
                columns: new[] { "GuestPlayerId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_live_sessions_GuestPlayerId_State",
                table: "live_sessions",
                columns: new[] { "GuestPlayerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_live_sessions_HostPlayerId_State",
                table: "live_sessions",
                columns: new[] { "HostPlayerId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "live_sessions");

            migrationBuilder.DropColumn(
                name: "AutoAcceptLive",
                table: "players");
        }
    }
}
