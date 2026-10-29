using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class EconomyHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_wallets_balance",
                table: "wallets");

            migrationBuilder.CreateTable(
                name: "forbidden_defs",
                columns: table => new
                {
                    DefName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_forbidden_defs", x => x.DefName);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forbidden_defs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_wallets_balance",
                table: "wallets",
                sql: "\"Balance\" >= 0");
        }
    }
}
