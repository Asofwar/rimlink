using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryCorrectness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_transfers_ClientRequestId",
                table: "transfers");

            migrationBuilder.DropIndex(
                name: "IX_trades_ClientRequestId",
                table: "trades");

            migrationBuilder.DropIndex(
                name: "IX_parcels_ClientRequestId",
                table: "parcels");

            migrationBuilder.DropIndex(
                name: "IX_lots_ClientRequestId",
                table: "lots");

            migrationBuilder.AddColumn<byte[]>(
                name: "RequestIntent",
                table: "transfers",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RequestTargetId",
                table: "transfers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RequestIntent",
                table: "trades",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RawSha256",
                table: "save_versions",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryJson",
                table: "save_versions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "RecoveryVersion",
                table: "save_versions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<long>(
                name: "SaveGeneration",
                table: "save_versions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SaveWriteEpoch",
                table: "save_versions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotId",
                table: "save_versions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "RecoveryVersion",
                table: "players",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<long>(
                name: "SaveWriteEpoch",
                table: "players",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "DeliveredGeneration",
                table: "parcels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "DeliveryRecoveryVersion",
                table: "parcels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<long>(
                name: "RequestActorId",
                table: "parcels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RequestIntent",
                table: "parcels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestKind",
                table: "parcels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RequestIntent",
                table: "lots",
                type: "BLOB",
                nullable: true);

            // Preserve original keys; bind only operations whose owner is recorded in the old row.
            migrationBuilder.Sql("""
                UPDATE parcels SET RequestActorId=FromPlayerId, RequestKind='transfer.send'
                WHERE ClientRequestId IS NOT NULL AND Kind=0 AND FromPlayerId IS NOT NULL;
                UPDATE parcels SET RequestActorId=ToPlayerId, RequestKind='wallet.withdraw'
                WHERE ClientRequestId IS NOT NULL AND Kind=3;
                UPDATE parcels SET RequestActorId=ToPlayerId, RequestKind='shop.buy'
                WHERE ClientRequestId IS NOT NULL AND Kind=1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_transfers_FromPlayerId_Kind_ClientRequestId",
                table: "transfers",
                columns: new[] { "FromPlayerId", "Kind", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_trades_InitiatorPlayerId_ClientRequestId",
                table: "trades",
                columns: new[] { "InitiatorPlayerId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_save_versions_PlayerId_SnapshotId",
                table: "save_versions",
                columns: new[] { "PlayerId", "SnapshotId" },
                unique: true,
                filter: "\"SnapshotId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_parcels_RequestActorId_RequestKind_ClientRequestId",
                table: "parcels",
                columns: new[] { "RequestActorId", "RequestKind", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_lots_SellerPlayerId_ClientRequestId",
                table: "lots",
                columns: new[] { "SellerPlayerId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_transfers_FromPlayerId_Kind_ClientRequestId",
                table: "transfers");

            migrationBuilder.DropIndex(
                name: "IX_trades_InitiatorPlayerId_ClientRequestId",
                table: "trades");

            migrationBuilder.DropIndex(
                name: "IX_save_versions_PlayerId_SnapshotId",
                table: "save_versions");

            migrationBuilder.DropIndex(
                name: "IX_parcels_RequestActorId_RequestKind_ClientRequestId",
                table: "parcels");

            migrationBuilder.DropIndex(
                name: "IX_lots_SellerPlayerId_ClientRequestId",
                table: "lots");

            migrationBuilder.DropColumn(
                name: "RequestIntent",
                table: "transfers");

            migrationBuilder.DropColumn(
                name: "RequestTargetId",
                table: "transfers");

            migrationBuilder.DropColumn(
                name: "RequestIntent",
                table: "trades");

            migrationBuilder.DropColumn(
                name: "RawSha256",
                table: "save_versions");

            migrationBuilder.DropColumn(
                name: "RecoveryJson",
                table: "save_versions");

            migrationBuilder.DropColumn(
                name: "RecoveryVersion",
                table: "save_versions");

            migrationBuilder.DropColumn(
                name: "SaveGeneration",
                table: "save_versions");

            migrationBuilder.DropColumn(
                name: "SaveWriteEpoch",
                table: "save_versions");

            migrationBuilder.DropColumn(
                name: "SnapshotId",
                table: "save_versions");

            migrationBuilder.DropColumn(
                name: "RecoveryVersion",
                table: "players");

            migrationBuilder.DropColumn(
                name: "SaveWriteEpoch",
                table: "players");

            migrationBuilder.DropColumn(
                name: "DeliveredGeneration",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "DeliveryRecoveryVersion",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "RequestActorId",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "RequestIntent",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "RequestKind",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "RequestIntent",
                table: "lots");

            migrationBuilder.CreateIndex(
                name: "IX_transfers_ClientRequestId",
                table: "transfers",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_trades_ClientRequestId",
                table: "trades",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_parcels_ClientRequestId",
                table: "parcels",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_lots_ClientRequestId",
                table: "lots",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");
        }
    }
}
