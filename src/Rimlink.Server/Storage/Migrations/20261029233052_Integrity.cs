using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Integrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DevMode",
                table: "save_versions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "WealthItems",
                table: "save_versions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "WealthTotal",
                table: "save_versions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "EconomyFreezeReason",
                table: "players",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EconomyFrozenAt",
                table: "players",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DevMode",
                table: "save_versions");

            migrationBuilder.DropColumn(
                name: "WealthItems",
                table: "save_versions");

            migrationBuilder.DropColumn(
                name: "WealthTotal",
                table: "save_versions");

            migrationBuilder.DropColumn(
                name: "EconomyFreezeReason",
                table: "players");

            migrationBuilder.DropColumn(
                name: "EconomyFrozenAt",
                table: "players");
        }
    }
}
