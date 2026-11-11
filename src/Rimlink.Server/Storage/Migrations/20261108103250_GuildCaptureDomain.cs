using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class GuildCaptureDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guild_capture_operations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    IntentDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Receipt = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TransitionId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_capture_operations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "guild_outposts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    Tile = table.Column<int>(type: "INTEGER", nullable: false),
                    Resource = table.Column<int>(type: "INTEGER", nullable: false),
                    AllianceId = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Stock = table.Column<int>(type: "INTEGER", nullable: false),
                    Remainder = table.Column<long>(type: "INTEGER", nullable: false),
                    CheckpointUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClockProcessId = table.Column<string>(type: "TEXT", nullable: false),
                    ClockTimestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    ClockFrequency = table.Column<long>(type: "INTEGER", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", nullable: false),
                    DefenceHourUtc = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingDefenceHourUtc = table.Column<int>(type: "INTEGER", nullable: true),
                    WindowChangeAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProtectedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsecutiveAborts = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_outposts", x => x.Id);
                    table.CheckConstraint("CK_guild_outpost_bounds", "Tile >= 0 AND Revision >= 0 AND ConsecutiveAborts >= 0");
                    table.CheckConstraint("CK_guild_resource", "Resource IN (1,2)");
                    table.CheckConstraint("CK_guild_stock", "Stock BETWEEN 0 AND 1000 AND Remainder >= 0 AND Remainder < 36000000000 AND (Stock < 1000 OR Remainder = 0)");
                    table.CheckConstraint("CK_guild_window", "DefenceHourUtc BETWEEN 0 AND 23 AND (PendingDefenceHourUtc IS NULL OR PendingDefenceHourUtc BETWEEN 0 AND 23)");
                    table.ForeignKey(
                        name: "FK_guild_outposts_alliance_groups_AllianceId",
                        column: x => x.AllianceId,
                        principalTable: "alliance_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_resource_claims",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    IntentDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ActorId = table.Column<long>(type: "INTEGER", nullable: false),
                    AllianceId = table.Column<long>(type: "INTEGER", nullable: false),
                    OriginKind = table.Column<string>(type: "TEXT", nullable: false),
                    OriginId = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    Boundary = table.Column<string>(type: "TEXT", nullable: false),
                    RecipientId = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveWriteEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    Resource = table.Column<int>(type: "INTEGER", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    ManifestId = table.Column<string>(type: "TEXT", nullable: false),
                    ValuationRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    TransitionId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_resource_claims", x => x.Id);
                    table.CheckConstraint("CK_guild_claim", "Resource IN (1,2) AND Count BETWEEN 1 AND 1000 AND RecipientId > 0 AND SaveGeneration >= 0 AND SaveWriteEpoch >= 0");
                    table.ForeignKey(
                        name: "FK_guild_resource_claims_alliance_groups_AllianceId",
                        column: x => x.AllianceId,
                        principalTable: "alliance_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guild_resource_claims_players_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_campaigns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OutpostId = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    Boundary = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    AttackerAllianceId = table.Column<long>(type: "INTEGER", nullable: false),
                    DefenderAllianceId = table.Column<long>(type: "INTEGER", nullable: false),
                    AttackerCommanderId = table.Column<long>(type: "INTEGER", nullable: false),
                    DefenderCommanderId = table.Column<long>(type: "INTEGER", nullable: true),
                    DeclaredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScheduledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProductionCutUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RulesDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", nullable: false),
                    TerminalReason = table.Column<string>(type: "TEXT", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_campaigns", x => x.Id);
                    table.CheckConstraint("CK_guild_campaign_sides", "AttackerAllianceId > 0 AND DefenderAllianceId > 0 AND AttackerAllianceId <> DefenderAllianceId");
                    table.ForeignKey(
                        name: "FK_guild_campaigns_alliance_groups_AttackerAllianceId",
                        column: x => x.AttackerAllianceId,
                        principalTable: "alliance_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guild_campaigns_alliance_groups_DefenderAllianceId",
                        column: x => x.DefenderAllianceId,
                        principalTable: "alliance_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guild_campaigns_guild_outposts_OutpostId",
                        column: x => x.OutpostId,
                        principalTable: "guild_outposts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_capture_consents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OutpostId = table.Column<string>(type: "TEXT", nullable: false),
                    AllianceId = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaderId = table.Column<long>(type: "INTEGER", nullable: false),
                    OwnerRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    RulesVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenceHourUtc = table.Column<int>(type: "INTEGER", nullable: false),
                    Digest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_capture_consents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_guild_capture_consents_guild_outposts_OutpostId",
                        column: x => x.OutpostId,
                        principalTable: "guild_outposts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_resource_obligations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OutpostId = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    CampaignId = table.Column<string>(type: "TEXT", nullable: true),
                    TransitionId = table.Column<string>(type: "TEXT", nullable: false),
                    AllianceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Resource = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RemainingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ReservedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_resource_obligations", x => x.Id);
                    table.CheckConstraint("CK_guild_obligation", "Resource IN (1,2) AND OriginalCount BETWEEN 1 AND 1000 AND RemainingCount BETWEEN 0 AND OriginalCount AND ReservedCount BETWEEN 0 AND RemainingCount");
                    table.ForeignKey(
                        name: "FK_guild_resource_obligations_alliance_groups_AllianceId",
                        column: x => x.AllianceId,
                        principalTable: "alliance_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guild_resource_obligations_guild_outposts_OutpostId",
                        column: x => x.OutpostId,
                        principalTable: "guild_outposts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_parcel_links",
                columns: table => new
                {
                    ParcelId = table.Column<long>(type: "INTEGER", nullable: false),
                    ClaimId = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PayloadDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ValueX100 = table.Column<long>(type: "INTEGER", nullable: false),
                    AcceptedSnapshotId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_parcel_links", x => x.ParcelId);
                    table.ForeignKey(
                        name: "FK_guild_parcel_links_guild_resource_claims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "guild_resource_claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guild_parcel_links_parcels_ParcelId",
                        column: x => x.ParcelId,
                        principalTable: "parcels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_acquisition_reservations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<string>(type: "TEXT", nullable: false),
                    AllianceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_acquisition_reservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_guild_acquisition_reservations_guild_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "guild_campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_battle_slots",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutorId = table.Column<string>(type: "TEXT", nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_battle_slots", x => x.Id);
                    table.CheckConstraint("CK_guild_slot_interval", "EndsAtUtc > StartsAtUtc");
                    table.ForeignKey(
                        name: "FK_guild_battle_slots_guild_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "guild_campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_battles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    Boundary = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutorId = table.Column<string>(type: "TEXT", nullable: false),
                    Attempt = table.Column<long>(type: "INTEGER", nullable: false),
                    Fence = table.Column<long>(type: "INTEGER", nullable: false),
                    StartNonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    StartManifest = table.Column<byte[]>(type: "BLOB", nullable: true),
                    StartDigest = table.Column<byte[]>(type: "BLOB", nullable: true),
                    FinalManifest = table.Column<byte[]>(type: "BLOB", nullable: true),
                    FinalDigest = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Receipt = table.Column<byte[]>(type: "BLOB", nullable: true),
                    View = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ViewSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_battles", x => x.Id);
                    table.CheckConstraint("CK_guild_battle_fence", "Attempt > 0 AND Fence > 0 AND ViewSequence >= 0");
                    table.ForeignKey(
                        name: "FK_guild_battles_guild_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "guild_campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_delivery_attempts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ParcelId = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    IntentDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RecipientId = table.Column<long>(type: "INTEGER", nullable: false),
                    WorldId = table.Column<string>(type: "TEXT", nullable: false),
                    Boundary = table.Column<string>(type: "TEXT", nullable: false),
                    SaveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveWriteEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    BeforeSnapshotId = table.Column<string>(type: "TEXT", nullable: false),
                    BeforeSnapshotDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AcceptedSnapshotId = table.Column<string>(type: "TEXT", nullable: true),
                    Receipt = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_delivery_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_guild_delivery_attempts_guild_parcel_links_ParcelId",
                        column: x => x.ParcelId,
                        principalTable: "guild_parcel_links",
                        principalColumn: "ParcelId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_battle_artifacts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    BattleId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Digest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_battle_artifacts", x => x.Id);
                    table.CheckConstraint("CK_guild_artifact", "SizeBytes BETWEEN 1 AND 67108864 AND length(Digest) = 32");
                    table.ForeignKey(
                        name: "FK_guild_battle_artifacts_guild_battles_BattleId",
                        column: x => x.BattleId,
                        principalTable: "guild_battles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });


            migrationBuilder.CreateTable(
                name: "guild_battle_control_grants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    BattleId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Side = table.Column<int>(type: "INTEGER", nullable: false),
                    Epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    CapabilityDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_battle_control_grants", x => x.Id);
                    table.CheckConstraint("CK_guild_control_grant", "Side IN (1,2) AND Epoch > 0 AND length(CapabilityDigest) = 32");
                    table.ForeignKey(
                        name: "FK_guild_battle_control_grants_guild_battles_BattleId",
                        column: x => x.BattleId,
                        principalTable: "guild_battles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_guild_acquisition_reservations_AllianceId_Active",
                table: "guild_acquisition_reservations",
                columns: new[] { "AllianceId", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_guild_acquisition_reservations_CampaignId",
                table: "guild_acquisition_reservations",
                column: "CampaignId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guild_battle_artifacts_BattleId_Kind",
                table: "guild_battle_artifacts",
                columns: new[] { "BattleId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guild_battle_artifacts_RelativePath",
                table: "guild_battle_artifacts",
                column: "RelativePath",
                unique: true);



            migrationBuilder.CreateIndex(
                name: "IX_guild_battle_control_grants_BattleId_Side",
                table: "guild_battle_control_grants",
                columns: new[] { "BattleId", "Side" },
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_guild_battle_control_grants_BattleId_Side_Epoch",
                table: "guild_battle_control_grants",
                columns: new[] { "BattleId", "Side", "Epoch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guild_battle_slots_CampaignId",
                table: "guild_battle_slots",
                column: "CampaignId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guild_battle_slots_ExecutorId_StartsAtUtc_Active",
                table: "guild_battle_slots",
                columns: new[] { "ExecutorId", "StartsAtUtc", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_guild_battles_CampaignId",
                table: "guild_battles",
                column: "CampaignId",
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_guild_battles_CampaignId_Attempt",
                table: "guild_battles",
                columns: new[] { "CampaignId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guild_battles_ExecutorId",
                table: "guild_battles",
                column: "ExecutorId",
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_guild_campaigns_AttackerAllianceId",
                table: "guild_campaigns",
                column: "AttackerAllianceId",
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_guild_campaigns_DefenderAllianceId",
                table: "guild_campaigns",
                column: "DefenderAllianceId");

            migrationBuilder.CreateIndex(
                name: "IX_guild_campaigns_OutpostId",
                table: "guild_campaigns",
                column: "OutpostId",
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_guild_capture_consents_OutpostId_OwnerRevision",
                table: "guild_capture_consents",
                columns: new[] { "OutpostId", "OwnerRevision" },
                unique: true);
            foreach (string table in new[] { "guild_capture_consents", "guild_battle_artifacts", "guild_capture_operations" })
                migrationBuilder.Sql($"CREATE TRIGGER {table}_no_update BEFORE UPDATE ON {table}\nBEGIN SELECT RAISE(ABORT, 'guild evidence is immutable'); END;\n" +
                    $"CREATE TRIGGER {table}_no_delete BEFORE DELETE ON {table}\nBEGIN SELECT RAISE(ABORT, 'guild evidence is immutable'); END;");

            migrationBuilder.CreateIndex(
                name: "IX_guild_capture_operations_ActorId_RequestId",
                table: "guild_capture_operations",
                columns: new[] { "ActorId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guild_delivery_attempts_ParcelId",
                table: "guild_delivery_attempts",
                column: "ParcelId",
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_guild_delivery_attempts_RecipientId_WorldId_SaveGeneration_Boundary_RequestId",
                table: "guild_delivery_attempts",
                columns: new[] { "RecipientId", "WorldId", "SaveGeneration", "Boundary", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guild_outposts_AllianceId",
                table: "guild_outposts",
                column: "AllianceId");

            migrationBuilder.CreateIndex(
                name: "IX_guild_outposts_WorldId_AllianceId",
                table: "guild_outposts",
                columns: new[] { "WorldId", "AllianceId" });

            migrationBuilder.CreateIndex(
                name: "IX_guild_outposts_WorldId_Tile",
                table: "guild_outposts",
                columns: new[] { "WorldId", "Tile" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guild_parcel_links_ClaimId",
                table: "guild_parcel_links",
                column: "ClaimId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guild_resource_claims_ActorId_RequestId",
                table: "guild_resource_claims",
                columns: new[] { "ActorId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guild_resource_claims_AllianceId",
                table: "guild_resource_claims",
                column: "AllianceId");

            migrationBuilder.CreateIndex(
                name: "IX_guild_resource_claims_OriginKind_OriginId",
                table: "guild_resource_claims",
                columns: new[] { "OriginKind", "OriginId" });

            migrationBuilder.CreateIndex(
                name: "IX_guild_resource_claims_RecipientId",
                table: "guild_resource_claims",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_guild_resource_obligations_AllianceId_RemainingCount",
                table: "guild_resource_obligations",
                columns: new[] { "AllianceId", "RemainingCount" });

            migrationBuilder.CreateIndex(
                name: "IX_guild_resource_obligations_OutpostId_OwnerRevision",
                table: "guild_resource_obligations",
                columns: new[] { "OutpostId", "OwnerRevision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guild_acquisition_reservations");

            migrationBuilder.DropTable(
                name: "guild_battle_artifacts");


            migrationBuilder.DropTable(
                name: "guild_battle_control_grants");

            migrationBuilder.DropTable(
                name: "guild_battle_slots");

            migrationBuilder.DropTable(
                name: "guild_capture_consents");

            migrationBuilder.DropTable(
                name: "guild_capture_operations");

            migrationBuilder.DropTable(
                name: "guild_delivery_attempts");

            migrationBuilder.DropTable(
                name: "guild_resource_obligations");

            migrationBuilder.DropTable(
                name: "guild_battles");

            migrationBuilder.DropTable(
                name: "guild_parcel_links");

            migrationBuilder.DropTable(
                name: "guild_campaigns");

            migrationBuilder.DropTable(
                name: "guild_resource_claims");

            migrationBuilder.DropTable(
                name: "guild_outposts");
        }
    }
}
