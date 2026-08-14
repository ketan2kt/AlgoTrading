using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF migration source is generated and executed once.

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistPaperExitFollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "paper_exit_follow_ups",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TradeResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: false),
                    HorizonMinutes = table.Column<int>(type: "integer", nullable: false),
                    ObservedOptionPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    HypotheticalPnlFromEntry = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IncrementalPnlAfterExit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ScheduledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_exit_follow_ups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_paper_exit_follow_ups_ObservedAtUtc",
                schema: "trading",
                table: "paper_exit_follow_ups",
                column: "ObservedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_paper_exit_follow_ups_TradeResultId_HorizonMinutes",
                schema: "trading",
                table: "paper_exit_follow_ups",
                columns: new[] { "TradeResultId", "HorizonMinutes" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paper_exit_follow_ups",
                schema: "trading");
        }
    }
}
#pragma warning restore CA1861
