using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingSystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradingDbContext))]
[Migration("20261012090000_RemoveScalpPaperTradingData")]
public sealed class RemoveScalpPaperTradingData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM trading.market_strategy_audits
            WHERE "Market" IN ('nifty-scalp', 'sensex-scalp');

            DELETE FROM trading.market_paper_positions
            WHERE "Market" IN ('nifty-scalp', 'sensex-scalp');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Scalp paper records are intentionally removed and cannot be reconstructed.
    }
}
