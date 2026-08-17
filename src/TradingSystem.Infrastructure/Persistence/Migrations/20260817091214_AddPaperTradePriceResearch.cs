using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF migration source is generated and executed once.

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperTradePriceResearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "paper_trade_price_samples",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OptionPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ObservedMinuteUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_trade_price_samples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_paper_trade_price_samples_InstrumentId_ObservedAtUtc",
                schema: "trading",
                table: "paper_trade_price_samples",
                columns: new[] { "InstrumentId", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_trade_price_samples_SignalId_ObservedMinuteUtc",
                schema: "trading",
                table: "paper_trade_price_samples",
                columns: new[] { "SignalId", "ObservedMinuteUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paper_trade_price_samples",
                schema: "trading");
        }
    }
}
