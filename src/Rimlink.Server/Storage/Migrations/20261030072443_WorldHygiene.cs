using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class WorldHygiene : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSessionEndAt",
                table: "accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "archived_colonies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Tile = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SettledAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ArchivedBy = table.Column<string>(type: "TEXT", nullable: false),
                    WealthTotal = table.Column<long>(type: "INTEGER", nullable: false),
                    ColonyGameDays = table.Column<int>(type: "INTEGER", nullable: true),
                    LastActiveAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Dir = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    RestoredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RestoredBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archived_colonies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_archived_colonies_players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_actions_TargetAccountId_At",
                table: "admin_actions",
                columns: new[] { "TargetAccountId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_archived_colonies_PlayerId_State",
                table: "archived_colonies",
                columns: new[] { "PlayerId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archived_colonies");

            migrationBuilder.DropIndex(
                name: "IX_admin_actions_TargetAccountId_At",
                table: "admin_actions");

            migrationBuilder.DropColumn(
                name: "LastSessionEndAt",
                table: "accounts");
        }
    }
}
