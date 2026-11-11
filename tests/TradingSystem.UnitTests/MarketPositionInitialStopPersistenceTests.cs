using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.UnitTests;

public sealed class MarketPositionInitialStopPersistenceTests
{
    [Fact]
    public void InitialStopMigrationAddsNullableColumnWithoutRewritingLegacyPositions()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_model")
            .Options;
        using var db = new TradingDbContext(options, TimeProvider.System);
        var initialStop = db.Model.FindEntityType(typeof(MarketPaperPosition))!
            .FindProperty(nameof(MarketPaperPosition.InitialStopLoss))!;
        Assert.True(initialStop.IsNullable);
        Assert.Equal(18, initialStop.GetPrecision());
        Assert.Equal(4, initialStop.GetScale());
        var sql = db.GetService<IMigrator>().GenerateScript("20261105180000_ExcursionGrossBasis",
            "20261111161000_PersistBankNiftyTrailFloor");
        Assert.Contains("ADD \"InitialStopLoss\" numeric(18,4);", sql, StringComparison.Ordinal);
        Assert.Contains("ADD \"BankNiftyTrailFloorNet\" numeric(18,4);", sql, StringComparison.Ordinal);
        Assert.True(db.Model.FindEntityType(typeof(MarketPaperPosition))!
            .FindProperty(nameof(MarketPaperPosition.BankNiftyTrailFloorNet))!.IsNullable);
        Assert.DoesNotContain("UPDATE trading.market_paper_positions", sql, StringComparison.OrdinalIgnoreCase);
    }
}
