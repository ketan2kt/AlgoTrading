using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingSystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradingDbContext))]
[Migration("20261026120000_AddTradeExcursionTelemetry")]
public sealed class AddTradeExcursionTelemetry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(name: "IX_paper_trade_price_samples_SignalId_OptionPrice",
            schema: "trading", table: "paper_trade_price_samples",
            columns: new[] { "SignalId", "OptionPrice" });
        migrationBuilder.AddColumn<decimal>(name: "MaximumFavourablePnl", schema: "trading",
            table: "market_paper_positions", type: "numeric(18,4)", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "MaximumFavourableAtUtc", schema: "trading",
            table: "market_paper_positions", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "MaximumAdversePnl", schema: "trading",
            table: "market_paper_positions", type: "numeric(18,4)", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "MaximumAdverseAtUtc", schema: "trading",
            table: "market_paper_positions", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<int>(name: "ExcursionSampleCount", schema: "trading",
            table: "market_paper_positions", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "ExcursionObservedFromUtc", schema: "trading",
            table: "market_paper_positions", type: "timestamp with time zone", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_paper_trade_price_samples_SignalId_OptionPrice",
            schema: "trading", table: "paper_trade_price_samples");
        migrationBuilder.DropColumn(name: "MaximumFavourablePnl", schema: "trading",
            table: "market_paper_positions");
        migrationBuilder.DropColumn(name: "MaximumFavourableAtUtc", schema: "trading",
            table: "market_paper_positions");
        migrationBuilder.DropColumn(name: "MaximumAdversePnl", schema: "trading",
            table: "market_paper_positions");
        migrationBuilder.DropColumn(name: "MaximumAdverseAtUtc", schema: "trading",
            table: "market_paper_positions");
        migrationBuilder.DropColumn(name: "ExcursionSampleCount", schema: "trading",
            table: "market_paper_positions");
        migrationBuilder.DropColumn(name: "ExcursionObservedFromUtc", schema: "trading",
            table: "market_paper_positions");
    }
}
