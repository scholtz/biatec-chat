using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Biatec.Chat.Api.Migrations
{
    /// <inheritdoc />
    public partial class EvidenceAnchorRefTxId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows keep the lower-case "D" GUID text they were hashed with (uuid::text is exactly that), so the
            // hash chain of already written entries stays valid. They are legacy: their anchorRef is not a transaction ID.
            // New entries store the Algorand transaction ID (T-081).
            migrationBuilder.AlterColumn<string>(
                name: "AnchorRef",
                table: "EvidenceEntries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "AnchorRef",
                table: "EvidenceEntries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }
    }
}
