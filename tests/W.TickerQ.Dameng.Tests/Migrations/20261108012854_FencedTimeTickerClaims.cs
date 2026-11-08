#if TICKERQ_F14
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace W.TickerQ.Dameng.Tests.Migrations
{
    /// <inheritdoc />
    public partial class FencedTimeTickerClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClaimStarted",
                table: "TimeTickers",
                type: "BIT",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                table: "TimeTickers",
                type: "CHAR(36)",
                nullable: false,
                defaultValue: Guid.Empty);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimStarted",
                table: "TimeTickers");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                table: "TimeTickers");
        }
    }
}

#endif
