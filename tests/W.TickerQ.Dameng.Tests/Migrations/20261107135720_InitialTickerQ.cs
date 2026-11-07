using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace W.TickerQ.Dameng.Tests.Migrations
{
    /// <inheritdoc />
    public partial class InitialTickerQ : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CronTickers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "CHAR(36)", nullable: false),
                    Expression = table.Column<string>(type: "NVARCHAR2(512)", maxLength: 512, nullable: true),
                    Request = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Retries = table.Column<int>(type: "INT", nullable: false),
                    RetryIntervals = table.Column<string>(type: "CLOB", nullable: true),
                    Function = table.Column<string>(type: "NVARCHAR2(256)", maxLength: 256, nullable: true),
                    Description = table.Column<string>(type: "NCLOB", nullable: true),
                    InitIdentifier = table.Column<string>(type: "NCLOB", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CronTickers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimeTickers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "CHAR(36)", nullable: false),
                    Function = table.Column<string>(type: "NCLOB", nullable: true),
                    Description = table.Column<string>(type: "NCLOB", nullable: true),
                    InitIdentifier = table.Column<string>(type: "NCLOB", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: false),
                    Status = table.Column<int>(type: "INT", nullable: false),
                    LockHolder = table.Column<string>(type: "NVARCHAR2(256)", maxLength: 256, nullable: true),
                    Request = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ExecutionTime = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: true),
                    LockedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: true),
                    ExecutedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: true),
                    ExceptionMessage = table.Column<string>(type: "NCLOB", nullable: true),
                    SkippedReason = table.Column<string>(type: "NCLOB", nullable: true),
                    ElapsedTime = table.Column<long>(type: "BIGINT", nullable: false),
                    Retries = table.Column<int>(type: "INT", nullable: false),
                    RetryCount = table.Column<int>(type: "INT", nullable: false),
                    RetryIntervals = table.Column<string>(type: "CLOB", nullable: true),
                    ParentId = table.Column<Guid>(type: "CHAR(36)", nullable: true),
                    RunCondition = table.Column<int>(type: "INT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeTickers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeTickers_TimeTickers_ParentId",
                        column: x => x.ParentId,
                        principalTable: "TimeTickers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CronTickerOccurrences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "CHAR(36)", nullable: false),
                    Status = table.Column<int>(type: "INT", nullable: false),
                    LockHolder = table.Column<string>(type: "NVARCHAR2(256)", maxLength: 256, nullable: true),
                    ExecutionTime = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: false),
                    CronTickerId = table.Column<Guid>(type: "CHAR(36)", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: true),
                    ExecutedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: true),
                    ExceptionMessage = table.Column<string>(type: "NCLOB", nullable: true),
                    SkippedReason = table.Column<string>(type: "NCLOB", nullable: true),
                    ElapsedTime = table.Column<long>(type: "BIGINT", nullable: false),
                    RetryCount = table.Column<int>(type: "INT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", precision: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CronTickerOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CronTickerOccurrences_CronTickers_CronTickerId",
                        column: x => x.CronTickerId,
                        principalTable: "CronTickers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CronTickerOccurrence_CronTickerId",
                table: "CronTickerOccurrences",
                column: "CronTickerId");

            migrationBuilder.CreateIndex(
                name: "IX_CronTickerOccurrence_ExecutionTime",
                table: "CronTickerOccurrences",
                column: "ExecutionTime");

            migrationBuilder.CreateIndex(
                name: "IX_CronTickerOccurrence_Status_ExecutionTime",
                table: "CronTickerOccurrences",
                columns: new[] { "Status", "ExecutionTime" });

            migrationBuilder.CreateIndex(
                name: "UQ_CronTickerId_ExecutionTime",
                table: "CronTickerOccurrences",
                columns: new[] { "CronTickerId", "ExecutionTime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CronTickers_Expression",
                table: "CronTickers",
                column: "Expression");

            migrationBuilder.CreateIndex(
                name: "IX_Function_Expression",
                table: "CronTickers",
                columns: new[] { "Function", "Expression" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeTicker_ExecutionTime",
                table: "TimeTickers",
                column: "ExecutionTime");

            migrationBuilder.CreateIndex(
                name: "IX_TimeTicker_Status_ExecutionTime",
                table: "TimeTickers",
                columns: new[] { "Status", "ExecutionTime" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeTickers_ParentId",
                table: "TimeTickers",
                column: "ParentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CronTickerOccurrences");

            migrationBuilder.DropTable(
                name: "TimeTickers");

            migrationBuilder.DropTable(
                name: "CronTickers");
        }
    }
}
