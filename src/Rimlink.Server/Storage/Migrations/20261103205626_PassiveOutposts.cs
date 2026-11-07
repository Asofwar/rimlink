using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PassiveOutposts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outpost_assignments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OutpostId = table.Column<string>(type: "TEXT", nullable: false),
                    PawnCustodyId = table.Column<string>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    Blob = table.Column<byte[]>(type: "BLOB", nullable: false),
                    BlobDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    FactsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PreparedOperationId = table.Column<string>(type: "TEXT", nullable: false),
                    ReturnBatchId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_assignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outpost_authority",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    FormatVersion = table.Column<uint>(type: "INTEGER", nullable: false),
                    InstallationId = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    RestoreBoundary = table.Column<string>(type: "TEXT", nullable: false),
                    HeadId = table.Column<string>(type: "TEXT", nullable: false),
                    HeadDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TransitionId = table.Column<string>(type: "TEXT", nullable: true),
                    Phase = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_authority", x => x.Id);
                    table.CheckConstraint("CK_outpost_authority_singleton", "Id = 1");
                });

            migrationBuilder.CreateTable(
                name: "outpost_delivery_attempts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveWriteEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    RestoreBoundary = table.Column<string>(type: "TEXT", nullable: false),
                    ParcelId = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    IntentDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PayloadDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CanonicalSnapshotId = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalSnapshotHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    TransitionId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_delivery_attempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outpost_lifecycle_operations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    RestoreBoundary = table.Column<string>(type: "TEXT", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", nullable: false),
                    Intent = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IntentDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Progress = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Result = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_lifecycle_operations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outpost_manifests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Digest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Document = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Confirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_manifests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outpost_operations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveWriteEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    RestoreBoundary = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Intent = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IntentDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Result = table.Column<byte[]>(type: "BLOB", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    OutpostId = table.Column<string>(type: "TEXT", nullable: true),
                    TransitionId = table.Column<string>(type: "TEXT", nullable: true),
                    ReservedSilver = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_operations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outpost_parcel_links",
                columns: table => new
                {
                    ParcelId = table.Column<long>(type: "INTEGER", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    RestoreBoundary = table.Column<string>(type: "TEXT", nullable: false),
                    BatchId = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PayloadDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TransitionId = table.Column<string>(type: "TEXT", nullable: false),
                    AcceptedSnapshotId = table.Column<string>(type: "TEXT", nullable: true),
                    ValueX100 = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_parcel_links", x => x.ParcelId);
                });

            migrationBuilder.CreateTable(
                name: "outpost_reservations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    Tile = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_reservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outpost_restore_authorizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RestoreBoundary = table.Column<string>(type: "TEXT", nullable: false),
                    ParcelId = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CanonicalSnapshotId = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalSnapshotHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TransitionId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_restore_authorizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outpost_snapshot_links",
                columns: table => new
                {
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: false),
                    SaveWriteEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    RestoreBoundary = table.Column<string>(type: "TEXT", nullable: false),
                    RawSha256 = table.Column<byte[]>(type: "BLOB", nullable: false),
                    MetadataVersion = table.Column<uint>(type: "INTEGER", nullable: false),
                    Metadata = table.Column<byte[]>(type: "BLOB", nullable: false),
                    FrontierDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TransitionId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_snapshot_links", x => new { x.WorldId, x.PlayerId, x.SaveGeneration, x.SnapshotId });
                });

            migrationBuilder.CreateTable(
                name: "outpost_transitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    RestoreBoundary = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    PredecessorId = table.Column<string>(type: "TEXT", nullable: true),
                    PawnCustodyId = table.Column<string>(type: "TEXT", nullable: true),
                    OutpostId = table.Column<string>(type: "TEXT", nullable: true),
                    OldHeadId = table.Column<string>(type: "TEXT", nullable: false),
                    NextHeadId = table.Column<string>(type: "TEXT", nullable: false),
                    Digest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Intent = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Effects = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_transitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_outpost_transitions_outpost_transitions_PredecessorId",
                        column: x => x.PredecessorId,
                        principalTable: "outpost_transitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outposts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    RestoreBoundary = table.Column<string>(type: "TEXT", nullable: false),
                    Tile = table.Column<int>(type: "INTEGER", nullable: false),
                    Profile = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    OccupiesSlot = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Stock = table.Column<int>(type: "INTEGER", nullable: false),
                    FractionNumerator = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FractionDenominator = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CheckpointUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClockProcessId = table.Column<string>(type: "TEXT", nullable: false),
                    ClockTimestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    ClockFrequency = table.Column<long>(type: "INTEGER", nullable: false),
                    ManifestId = table.Column<string>(type: "TEXT", nullable: false),
                    ManifestDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    LastTransitionId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outposts", x => x.Id);
                    table.CheckConstraint("CK_outpost_fraction_denominator", "length(FractionDenominator) BETWEEN 1 AND 256 AND FractionDenominator NOT GLOB '*[^0-9]*' AND substr(FractionDenominator,1,1) BETWEEN '1' AND '9'");
                    table.CheckConstraint("CK_outpost_fraction_numerator", "length(FractionNumerator) BETWEEN 1 AND 256 AND FractionNumerator NOT GLOB '*[^0-9]*' AND (FractionNumerator = '0' OR substr(FractionNumerator,1,1) BETWEEN '1' AND '9')");
                    table.CheckConstraint("CK_outpost_stock", "Stock >= 0 AND Stock <= 1000");
                });

            migrationBuilder.CreateTable(
                name: "pawn_custody",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    RestoreBoundary = table.Column<string>(type: "TEXT", nullable: false),
                    LocationKind = table.Column<string>(type: "TEXT", nullable: false),
                    LocationId = table.Column<string>(type: "TEXT", nullable: false),
                    AssignmentId = table.Column<string>(type: "TEXT", nullable: true),
                    FrontierTransitionId = table.Column<string>(type: "TEXT", nullable: false),
                    Tombstone = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pawn_custody", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outpost_assignments_OutpostId_Active",
                table: "outpost_assignments",
                columns: new[] { "OutpostId", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_outpost_assignments_PawnCustodyId",
                table: "outpost_assignments",
                column: "PawnCustodyId",
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_assignments_PreparedOperationId",
                table: "outpost_assignments",
                column: "PreparedOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_delivery_attempts_RestoreBoundary_ParcelId",
                table: "outpost_delivery_attempts",
                columns: new[] { "RestoreBoundary", "ParcelId" },
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_delivery_attempts_WorldId_PlayerId_SaveGeneration_RestoreBoundary_RequestId",
                table: "outpost_delivery_attempts",
                columns: new[] { "WorldId", "PlayerId", "SaveGeneration", "RestoreBoundary", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_lifecycle_operations_RestoreBoundary_Actor_RequestId",
                table: "outpost_lifecycle_operations",
                columns: new[] { "RestoreBoundary", "Actor", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_manifests_WorldId",
                table: "outpost_manifests",
                column: "WorldId",
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_manifests_WorldId_Revision",
                table: "outpost_manifests",
                columns: new[] { "WorldId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_operations_OutpostId_State",
                table: "outpost_operations",
                columns: new[] { "OutpostId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_outpost_operations_WorldId_PlayerId_SaveGeneration_RestoreBoundary_RequestId",
                table: "outpost_operations",
                columns: new[] { "WorldId", "PlayerId", "SaveGeneration", "RestoreBoundary", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_parcel_links_BatchId",
                table: "outpost_parcel_links",
                column: "BatchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_parcel_links_WorldId_PlayerId_SaveGeneration",
                table: "outpost_parcel_links",
                columns: new[] { "WorldId", "PlayerId", "SaveGeneration" });

            migrationBuilder.CreateIndex(
                name: "IX_outpost_reservations_OperationId",
                table: "outpost_reservations",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_reservations_WorldId_PlayerId_SaveGeneration",
                table: "outpost_reservations",
                columns: new[] { "WorldId", "PlayerId", "SaveGeneration" },
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_reservations_WorldId_Tile",
                table: "outpost_reservations",
                columns: new[] { "WorldId", "Tile" },
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_restore_authorizations_RestoreBoundary_ParcelId",
                table: "outpost_restore_authorizations",
                columns: new[] { "RestoreBoundary", "ParcelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_snapshot_links_TransitionId",
                table: "outpost_snapshot_links",
                column: "TransitionId");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_transitions_NextHeadId",
                table: "outpost_transitions",
                column: "NextHeadId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_transitions_PawnCustodyId",
                table: "outpost_transitions",
                column: "PawnCustodyId");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_transitions_PredecessorId",
                table: "outpost_transitions",
                column: "PredecessorId");

            migrationBuilder.CreateIndex(
                name: "IX_outpost_transitions_WorldId_PlayerId_SaveGeneration",
                table: "outpost_transitions",
                columns: new[] { "WorldId", "PlayerId", "SaveGeneration" });

            migrationBuilder.CreateIndex(
                name: "IX_outposts_WorldId_PlayerId_SaveGeneration",
                table: "outposts",
                columns: new[] { "WorldId", "PlayerId", "SaveGeneration" },
                unique: true,
                filter: "OccupiesSlot = 1");

            migrationBuilder.CreateIndex(
                name: "IX_outposts_WorldId_Tile",
                table: "outposts",
                columns: new[] { "WorldId", "Tile" },
                unique: true,
                filter: "OccupiesSlot = 1");

            migrationBuilder.CreateIndex(
                name: "IX_pawn_custody_WorldId_PlayerId_SaveGeneration",
                table: "pawn_custody",
                columns: new[] { "WorldId", "PlayerId", "SaveGeneration" });

            // Evidence is append-only even when a maintenance path bypasses EF tracking.
            // The qualified schema preserves these literal CRLF bytes independently of source checkout endings.
            migrationBuilder.Sql(
                "CREATE TRIGGER outpost_transitions_no_update BEFORE UPDATE ON outpost_transitions\r\n" +
                "BEGIN SELECT RAISE(ABORT, 'outpost transition evidence is immutable'); END;\r\n" +
                "CREATE TRIGGER outpost_transitions_no_delete BEFORE DELETE ON outpost_transitions\r\n" +
                "BEGIN SELECT RAISE(ABORT, 'outpost transition evidence is immutable'); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outpost_assignments");

            migrationBuilder.DropTable(
                name: "outpost_authority");

            migrationBuilder.DropTable(
                name: "outpost_delivery_attempts");

            migrationBuilder.DropTable(
                name: "outpost_lifecycle_operations");

            migrationBuilder.DropTable(
                name: "outpost_manifests");

            migrationBuilder.DropTable(
                name: "outpost_operations");

            migrationBuilder.DropTable(
                name: "outpost_parcel_links");

            migrationBuilder.DropTable(
                name: "outpost_reservations");

            migrationBuilder.DropTable(
                name: "outpost_restore_authorizations");

            migrationBuilder.DropTable(
                name: "outpost_snapshot_links");

            migrationBuilder.DropTable(
                name: "outpost_transitions");

            migrationBuilder.DropTable(
                name: "outposts");

            migrationBuilder.DropTable(
                name: "pawn_custody");
        }
    }
}
