using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Biatec.Chat.Api.Migrations
{
    /// <inheritdoc />
    public partial class KnownUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnownUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OidcSub = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SignInCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnownUsers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnownUsers_LastSeenAt",
                table: "KnownUsers",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_KnownUsers_TenantId_OidcSub",
                table: "KnownUsers",
                columns: new[] { "TenantId", "OidcSub" },
                unique: true);

            // Users who signed in before this table existed: one row each, from their conversations (SignInCount 0 means
            // "not counted"). Erased conversations have no subject, so an erased user is not listed.
            migrationBuilder.Sql(@"
                INSERT INTO ""KnownUsers"" (""Id"", ""TenantId"", ""OidcSub"", ""FirstSeenAt"", ""LastSeenAt"", ""SignInCount"")
                SELECT gen_random_uuid(), ""TenantId"", ""OidcSub"", MIN(""CreatedAt""), MAX(""LastMessageAt""), 0
                FROM ""Conversations""
                WHERE ""OidcSub"" IS NOT NULL AND length(""OidcSub"") <= 256 AND length(""TenantId"") <= 128
                GROUP BY ""TenantId"", ""OidcSub"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnownUsers");
        }
    }
}
