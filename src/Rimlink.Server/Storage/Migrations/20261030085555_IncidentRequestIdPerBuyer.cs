using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class IncidentRequestIdPerBuyer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_incidents_ClientRequestId",
                table: "incidents");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_BuyerPlayerId_ClientRequestId",
                table: "incidents",
                columns: new[] { "BuyerPlayerId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_incidents_BuyerPlayerId_ClientRequestId",
                table: "incidents");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_ClientRequestId",
                table: "incidents",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");
        }
    }
}
