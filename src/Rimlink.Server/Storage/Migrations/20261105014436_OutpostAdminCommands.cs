using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class OutpostAdminCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outpost_admin_commands",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ActorKey = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Command = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CommandDigest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TransitionId = table.Column<string>(type: "TEXT", nullable: false),
                    NextHeadId = table.Column<string>(type: "TEXT", nullable: false),
                    Result = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AuditId = table.Column<long>(type: "INTEGER", nullable: false),
                    AuditProjection = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outpost_admin_commands", x => x.Id);
                    table.CheckConstraint("CK_outpost_admin_action", "length(Action) BETWEEN 1 AND 64");
                    table.CheckConstraint("CK_outpost_admin_actor", "length(ActorKey) = 64 AND ActorKey NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_outpost_admin_audit", "AuditId > 0 AND length(AuditProjection) BETWEEN 1 AND 65536");
                    table.CheckConstraint("CK_outpost_admin_command", "length(Command) BETWEEN 1 AND 65536 AND length(CommandDigest) = 32");
                    table.CheckConstraint("CK_outpost_admin_ids", "length(Id) = 36 AND length(RequestId) = 36 AND length(TransitionId) = 36 AND length(NextHeadId) = 36");
                    table.CheckConstraint("CK_outpost_admin_reason", "length(CAST(Reason AS BLOB)) BETWEEN 1 AND 1024");
                    table.CheckConstraint("CK_outpost_admin_result", "length(Result) BETWEEN 1 AND 65536");
                });

            migrationBuilder.CreateIndex(
                name: "IX_outpost_admin_commands_ActorKey_Action_RequestId",
                table: "outpost_admin_commands",
                columns: new[] { "ActorKey", "Action", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_admin_commands_AuditId",
                table: "outpost_admin_commands",
                column: "AuditId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_admin_commands_NextHeadId",
                table: "outpost_admin_commands",
                column: "NextHeadId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outpost_admin_commands_TransitionId",
                table: "outpost_admin_commands",
                column: "TransitionId",
                unique: true);

            // The qualified schema preserves these literal CRLF bytes independently of source checkout endings.
            migrationBuilder.Sql(
                "CREATE TRIGGER outpost_admin_commands_no_update BEFORE UPDATE ON outpost_admin_commands\r\n" +
                "BEGIN SELECT RAISE(ABORT, 'outpost admin command evidence is immutable'); END;\r\n" +
                "CREATE TRIGGER outpost_admin_commands_no_delete BEFORE DELETE ON outpost_admin_commands\r\n" +
                "BEGIN SELECT RAISE(ABORT, 'outpost admin command evidence is immutable'); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outpost_admin_commands");
        }
    }
}
