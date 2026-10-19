using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Biatec.Chat.Api.Migrations
{
    /// <inheritdoc />
    public partial class AnchorAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvidenceAnchorAttempts",
                columns: table => new
                {
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MerkleRoot = table.Column<string>(type: "text", nullable: false),
                    PreviousAnchorTxId = table.Column<string>(type: "text", nullable: true),
                    GapFrom = table.Column<string>(type: "text", nullable: true),
                    Leaves = table.Column<string>(type: "jsonb", nullable: false),
                    TxId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceAnchorAttempts", x => x.SequenceNumber);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvidenceAnchorAttempts");
        }
    }
}
