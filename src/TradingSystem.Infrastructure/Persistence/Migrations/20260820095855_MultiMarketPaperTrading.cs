using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiMarketPaperTrading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "market_paper_positions",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Market = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UnderlyingInstrumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionInstrumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Strategy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    StopLoss = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Target = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    RealisedPnl = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_paper_positions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "market_strategy_audits",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Market = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UnderlyingInstrumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandleTimeUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(7,6)", precision: 7, scale: 6, nullable: false),
                    ReasonsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_strategy_audits", x => x.Id);
                });

#pragma warning disable CA1861
            migrationBuilder.CreateIndex(
                name: "IX_market_paper_positions_Market_Status_OpenedAtUtc",
                schema: "trading",
                table: "market_paper_positions",
                columns: new[] { "Market", "Status", "OpenedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_market_strategy_audits_Market_CandleTimeUtc",
                schema: "trading",
                table: "market_strategy_audits",
                columns: new[] { "Market", "CandleTimeUtc" });
#pragma warning restore CA1861
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "market_paper_positions",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "market_strategy_audits",
                schema: "trading");
        }
    }
}
