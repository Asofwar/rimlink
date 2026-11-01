using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class NextFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ColonyPawnIds",
                table: "live_sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "alliance_audit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AllianceId = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<long>(type: "INTEGER", nullable: false),
                    SubjectId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Delta = table.Column<long>(type: "INTEGER", nullable: false),
                    Balance = table.Column<long>(type: "INTEGER", nullable: false),
                    DailyCap = table.Column<long>(type: "INTEGER", nullable: false),
                    MayWithdraw = table.Column<bool>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alliance_audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "alliance_groups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OwnerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Silver = table.Column<long>(type: "INTEGER", nullable: false),
                    Disbanded = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alliance_groups", x => x.Id);
                    table.CheckConstraint("CK_alliance_silver", "\"Silver\" >= 0");
                    table.ForeignKey(
                        name: "FK_alliance_groups_players_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alliance_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AllianceId = table.Column<long>(type: "INTEGER", nullable: false),
                    FromId = table.Column<long>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alliance_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuctionOperations",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IntentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Response = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuctionOperations", x => new { x.PlayerId, x.RequestId });
                    table.ForeignKey(
                        name: "FK_AuctionOperations_players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Auctions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SellerPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Items = table.Column<byte[]>(type: "BLOB", nullable: false),
                    StartPrice = table.Column<long>(type: "INTEGER", nullable: false),
                    BuyoutPrice = table.Column<long>(type: "INTEGER", nullable: false),
                    Bid = table.Column<long>(type: "INTEGER", nullable: false),
                    BidderPlayerId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MaxExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CloseReason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Auctions", x => x.Id);
                    table.CheckConstraint("CK_Auctions_Money", "StartPrice > 0 AND BuyoutPrice >= 0 AND Bid >= 0");
                    table.ForeignKey(
                        name: "FK_Auctions_players_BidderPlayerId",
                        column: x => x.BidderPlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Auctions_players_SellerPlayerId",
                        column: x => x.SellerPlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "colony_members",
                columns: table => new
                {
                    OwnerPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    MemberPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Accepted = table.Column<bool>(type: "INTEGER", nullable: false),
                    PawnIds = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_colony_members", x => new { x.OwnerPlayerId, x.MemberPlayerId });
                    table.ForeignKey(
                        name: "FK_colony_members_players_MemberPlayerId",
                        column: x => x.MemberPlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_colony_members_players_OwnerPlayerId",
                        column: x => x.OwnerPlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "community_invitations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromId = table.Column<long>(type: "INTEGER", nullable: false),
                    ToId = table.Column<long>(type: "INTEGER", nullable: false),
                    AllianceId = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_community_invitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_community_invitations_players_FromId",
                        column: x => x.FromId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_community_invitations_players_ToId",
                        column: x => x.ToId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "community_operations",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Intent = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Result = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_community_operations", x => new { x.PlayerId, x.RequestId });
                });

            migrationBuilder.CreateTable(
                name: "friendships",
                columns: table => new
                {
                    FirstId = table.Column<long>(type: "INTEGER", nullable: false),
                    SecondId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friendships", x => new { x.FirstId, x.SecondId });
                    table.ForeignKey(
                        name: "FK_friendships_players_FirstId",
                        column: x => x.FirstId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_friendships_players_SecondId",
                        column: x => x.SecondId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "player_profiles",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Bio = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    InvitationsOpen = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notifications = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_profiles", x => x.PlayerId);
                    table.ForeignKey(
                        name: "FK_player_profiles_players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "procurement_orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BuyerPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    SupplierPlayerId = table.Column<long>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    DefName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    Supplied = table.Column<int>(type: "INTEGER", nullable: false),
                    Reward = table.Column<long>(type: "INTEGER", nullable: false),
                    Escrow = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procurement_orders", x => x.Id);
                    table.CheckConstraint("CK_procurement_escrow", "Reward > 0 AND Escrow >= 0 AND Escrow <= 1000000000");
                    table.CheckConstraint("CK_procurement_units", "Quantity > 0 AND Quantity <= 1000000 AND Supplied >= 0 AND Supplied <= Quantity");
                    table.ForeignKey(
                        name: "FK_procurement_orders_players_BuyerPlayerId",
                        column: x => x.BuyerPlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_procurement_orders_players_SupplierPlayerId",
                        column: x => x.SupplierPlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alliance_members",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    AllianceId = table.Column<long>(type: "INTEGER", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MayWithdraw = table.Column<bool>(type: "INTEGER", nullable: false),
                    DailyCap = table.Column<long>(type: "INTEGER", nullable: false),
                    WithdrawalDay = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Withdrawn = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alliance_members", x => x.PlayerId);
                    table.ForeignKey(
                        name: "FK_alliance_members_alliance_groups_AllianceId",
                        column: x => x.AllianceId,
                        principalTable: "alliance_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alliance_members_players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "procurement_deliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<long>(type: "INTEGER", nullable: false),
                    SupplierPlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    Items = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Paid = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procurement_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_procurement_deliveries_players_SupplierPlayerId",
                        column: x => x.SupplierPlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_procurement_deliveries_procurement_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "procurement_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "procurement_requests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlayerId = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IntentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OrderId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procurement_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_procurement_requests_players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_procurement_requests_procurement_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "procurement_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alliance_audit_AllianceId_Id",
                table: "alliance_audit",
                columns: new[] { "AllianceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_alliance_groups_OwnerId",
                table: "alliance_groups",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_alliance_members_AllianceId",
                table: "alliance_members",
                column: "AllianceId");

            migrationBuilder.CreateIndex(
                name: "IX_alliance_messages_AllianceId_Id",
                table: "alliance_messages",
                columns: new[] { "AllianceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_BidderPlayerId_Status",
                table: "Auctions",
                columns: new[] { "BidderPlayerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_SellerPlayerId_Status",
                table: "Auctions",
                columns: new[] { "SellerPlayerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_Status_ExpiresAt",
                table: "Auctions",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_colony_members_MemberPlayerId",
                table: "colony_members",
                column: "MemberPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_community_invitations_FromId_State",
                table: "community_invitations",
                columns: new[] { "FromId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_community_invitations_ToId_State",
                table: "community_invitations",
                columns: new[] { "ToId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_friendships_SecondId",
                table: "friendships",
                column: "SecondId");

            migrationBuilder.CreateIndex(
                name: "IX_procurement_deliveries_OrderId",
                table: "procurement_deliveries",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_procurement_deliveries_SupplierPlayerId",
                table: "procurement_deliveries",
                column: "SupplierPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_procurement_orders_BuyerPlayerId",
                table: "procurement_orders",
                column: "BuyerPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_procurement_orders_Status_ExpiresAt",
                table: "procurement_orders",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_orders_SupplierPlayerId",
                table: "procurement_orders",
                column: "SupplierPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_procurement_requests_OrderId",
                table: "procurement_requests",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_procurement_requests_PlayerId_RequestId",
                table: "procurement_requests",
                columns: new[] { "PlayerId", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alliance_audit");

            migrationBuilder.DropTable(
                name: "alliance_members");

            migrationBuilder.DropTable(
                name: "alliance_messages");

            migrationBuilder.DropTable(
                name: "AuctionOperations");

            migrationBuilder.DropTable(
                name: "Auctions");

            migrationBuilder.DropTable(
                name: "colony_members");

            migrationBuilder.DropTable(
                name: "community_invitations");

            migrationBuilder.DropTable(
                name: "community_operations");

            migrationBuilder.DropTable(
                name: "friendships");

            migrationBuilder.DropTable(
                name: "player_profiles");

            migrationBuilder.DropTable(
                name: "procurement_deliveries");

            migrationBuilder.DropTable(
                name: "procurement_requests");

            migrationBuilder.DropTable(
                name: "alliance_groups");

            migrationBuilder.DropTable(
                name: "procurement_orders");

            migrationBuilder.DropColumn(
                name: "ColonyPawnIds",
                table: "live_sessions");
        }
    }
}
