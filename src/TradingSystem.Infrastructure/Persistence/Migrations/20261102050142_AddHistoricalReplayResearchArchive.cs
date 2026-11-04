using System;
using Microsoft.EntityFrameworkCore.Migrations;
#pragma warning disable CA1861 // EF migration scaffolding uses inline column arrays.

#nullable disable

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricalReplayResearchArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "historical_research_candles",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Underlying = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    GrowwSymbol = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OpenTimeUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    Open = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    High = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Low = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Close = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    OpenInterest = table.Column<decimal>(type: "numeric(24,4)", precision: 24, scale: 4, nullable: true),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_historical_research_candles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "historical_universe_page_receipts",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Market = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Page = table.Column<int>(type: "integer", nullable: false),
                    PageSize = table.Column<int>(type: "integer", nullable: false),
                    TotalSelectedContracts = table.Column<int>(type: "integer", nullable: false),
                    UniverseFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FetchedSymbolsJson = table.Column<string>(type: "jsonb", nullable: false),
                    FetchedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_historical_universe_page_receipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_historical_research_candles_Exchange_Underlying_OpenTimeUtc",
                schema: "trading",
                table: "historical_research_candles",
                columns: new[] { "Exchange", "Underlying", "OpenTimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_historical_research_candles_GrowwSymbol_OpenTimeUtc_Interva~",
                schema: "trading",
                table: "historical_research_candles",
                columns: new[] { "GrowwSymbol", "OpenTimeUtc", "IntervalSeconds", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_historical_universe_page_receipts_SessionDate_Market_Expiry~",
                schema: "trading",
                table: "historical_universe_page_receipts",
                columns: new[] { "SessionDate", "Market", "ExpiryDate", "Page", "PageSize" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "historical_research_candles",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "historical_universe_page_receipts",
                schema: "trading");
        }
    }
}
