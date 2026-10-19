using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Biatec.Chat.Api.Migrations
{
    /// <inheritdoc />
    public partial class KnownSafes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnownSafes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Network = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    DeployTxId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnownSafes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnownSafes_DeployTxId",
                table: "KnownSafes",
                column: "DeployTxId");

            migrationBuilder.CreateIndex(
                name: "IX_KnownSafes_TenantId_Network_AppId",
                table: "KnownSafes",
                columns: new[] { "TenantId", "Network", "AppId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnownSafes");
        }
    }
}
