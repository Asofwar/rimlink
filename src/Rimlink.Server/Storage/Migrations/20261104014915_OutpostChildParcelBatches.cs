using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class OutpostChildParcelBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outpost_parcel_links_BatchId",
                table: "outpost_parcel_links");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_parcel_links_BatchId",
                table: "outpost_parcel_links",
                column: "BatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outpost_parcel_links_BatchId",
                table: "outpost_parcel_links");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_parcel_links_BatchId",
                table: "outpost_parcel_links",
                column: "BatchId",
                unique: true);
        }
    }
}
