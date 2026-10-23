using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Biatec.Chat.Api.Migrations
{
    /// <inheritdoc />
    public partial class ConsentPollInterval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPolledAt",
                table: "ConsentRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PollIntervalSeconds",
                table: "ConsentRequests",
                type: "integer",
                nullable: false,
                defaultValue: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastPolledAt",
                table: "ConsentRequests");

            migrationBuilder.DropColumn(
                name: "PollIntervalSeconds",
                table: "ConsentRequests");
        }
    }
}
