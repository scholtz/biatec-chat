using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Biatec.Chat.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgentMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentMode",
                table: "ChatSessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Paused",
                table: "ChatSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentMode",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "Paused",
                table: "ChatSessions");
        }
    }
}
