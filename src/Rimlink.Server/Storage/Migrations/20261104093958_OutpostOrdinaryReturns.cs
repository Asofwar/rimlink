using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class OutpostOrdinaryReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outpost_ordinary_returns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveWriteEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    RestoreBoundary = table.Column<string>(type: "TEXT", nullable: false),
                    RequestKind = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    LocalOperationId = table.Column<string>(type: "TEXT", nullable: false),
                    DerivedRequestKind = table.Column<int>(type: "INTEGER", nullable: false),
                    DerivedRequestId = table.Column<string>(type: "TEXT", nullable: false),
                    Intent = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Payload = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PayloadDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    AcceptedSnapshotId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_ordinary_returns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outpost_ordinary_returns_PlayerId_DerivedRequestKind_DerivedRequestId",
                table: "outpost_ordinary_returns",
                columns: new[] { "PlayerId", "DerivedRequestKind", "DerivedRequestId" },
                unique: true,
                filter: "DerivedRequestKind <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_ordinary_returns_PlayerId_LocalOperationId",
                table: "outpost_ordinary_returns",
                columns: new[] { "PlayerId", "LocalOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_ordinary_returns_PlayerId_RequestKind_RequestId",
                table: "outpost_ordinary_returns",
                columns: new[] { "PlayerId", "RequestKind", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outpost_ordinary_returns");
        }
    }
}
