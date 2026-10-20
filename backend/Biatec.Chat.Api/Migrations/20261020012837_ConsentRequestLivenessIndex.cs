using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Biatec.Chat.Api.Migrations
{
    /// <inheritdoc />
    public partial class ConsentRequestLivenessIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConsentRequests_StepId",
                table: "ConsentRequests");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRequests_StepId_Status_ExpiresAt",
                table: "ConsentRequests",
                columns: new[] { "StepId", "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConsentRequests_StepId_Status_ExpiresAt",
                table: "ConsentRequests");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRequests_StepId",
                table: "ConsentRequests",
                column: "StepId");
        }
    }
}
