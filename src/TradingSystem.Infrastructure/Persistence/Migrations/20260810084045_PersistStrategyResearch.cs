using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistStrategyResearch : Migration
    {
        private static readonly string[] StrategyEvaluationIndexColumns =
            ["StrategyCode", "InstrumentId", "CandleTimeUtc"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "paper_trade_results",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TradingSymbol = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ExitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    GrossPnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EstimatedCosts = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RealisedPnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ExitReason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_trade_results", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "strategy_evaluations",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StrategyCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    StrategyVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandleTimeUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    OpeningRangeHigh = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    OpeningRangeLow = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Vwap = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    FastEma = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SlowEma = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AtrPercent = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    RelativeFuturesVolume = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    Regime = table.Column<int>(type: "integer", nullable: false),
                    RegimeBias = table.Column<int>(type: "integer", nullable: true),
                    RegimeConfidence = table.Column<decimal>(type: "numeric(7,6)", precision: 7, scale: 6, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    FailedConditionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: true),
                    OptionSymbol = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    OptionType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    OptionExpiry = table.Column<DateOnly>(type: "date", nullable: true),
                    OptionStrike = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    OptionPremium = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_strategy_evaluations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_paper_trade_results_ClosedAtUtc",
                schema: "trading",
                table: "paper_trade_results",
                column: "ClosedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_paper_trade_results_SignalId",
                schema: "trading",
                table: "paper_trade_results",
                column: "SignalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_strategy_evaluations_RecordedAtUtc",
                schema: "trading",
                table: "strategy_evaluations",
                column: "RecordedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_strategy_evaluations_StrategyCode_InstrumentId_CandleTimeUtc",
                schema: "trading",
                table: "strategy_evaluations",
                columns: StrategyEvaluationIndexColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paper_trade_results",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "strategy_evaluations",
                schema: "trading");
        }
    }
}
