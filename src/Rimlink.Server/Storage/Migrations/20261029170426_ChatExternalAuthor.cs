using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rimlink.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ChatExternalAuthor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalAuthor",
                table: "chat_messages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalAuthor",
                table: "chat_messages");
        }
    }
}
