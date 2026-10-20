using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Biatec.Chat.Api.Migrations
{
    /// <inheritdoc />
    public partial class RetentionTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ErasedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill: existing messages take their conversation's last message time (an upper bound, so retention
            // never clears a row early).
            migrationBuilder.Sql("""
                UPDATE "Messages" AS m SET "CreatedAt" = c."LastMessageAt"
                FROM "Conversations" AS c WHERE c."Id" = m."ConversationId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_CreatedAt",
                table: "Messages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceEntries_SignedAt",
                table: "EvidenceEntries",
                column: "SignedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_CreatedAt",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_EvidenceEntries_SignedAt",
                table: "EvidenceEntries");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ErasedAt",
                table: "Conversations");
        }
    }
}
