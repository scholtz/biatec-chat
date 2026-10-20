using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Biatec.Chat.Api.Migrations
{
    /// <inheritdoc />
    public partial class EvidenceSigningKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvidenceSigningKeys",
                columns: table => new
                {
                    Kid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    X = table.Column<byte[]>(type: "bytea", nullable: false),
                    Y = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceSigningKeys", x => x.Kid);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceSigningKeys_IsActive",
                table: "EvidenceSigningKeys",
                column: "IsActive",
                unique: true,
                filter: "\"IsActive\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvidenceSigningKeys");
        }
    }
}
