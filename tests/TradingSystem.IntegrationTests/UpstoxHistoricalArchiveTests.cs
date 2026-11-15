using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.IntegrationTests;

public sealed class UpstoxHistoricalArchiveTests
{
    [DockerFact]
    public async Task BackupCandlesKeepProviderProvenanceAndNeverWriteTradingCandles()
    {
        await using var container = new PostgreSqlBuilder("postgres:18.4-alpine").Build();
        await container.StartAsync(CancellationToken.None);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql(container.GetConnectionString()).Options, TimeProvider.System);
        await db.Database.MigrateAsync(CancellationToken.None);
        var importer = new GrowwHistoricalResearchImporter(db);
        var start = new DateTimeOffset(2026, 11, 16, 3, 45, 0, TimeSpan.Zero);
        foreach (var market in new[] { "NIFTY", "SENSEX", "BANKNIFTY" })
        {
            var instrument = new Instrument(Guid.NewGuid(), market == "SENSEX" ? "BSE" : "NSE", market,
                InstrumentSegment.Cash, InstrumentType.Index, start);
            instrument.UpdateIndexBrokerMetadata("1", market);
            var history = new GrowwHistoricalCandles([new("2026-11-16T09:15:00", 100, 110, 90, 105, 200, null)], 105, 1);
            var import = typeof(GrowwHistoricalResearchImporter).GetMethod("ImportBackupAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Task<int> Run() => (Task<int>)import.Invoke(importer, [instrument, market, new DateOnly(2026, 11, 16), history,
                start, start.AddMinutes(375), start.AddHours(8), CancellationToken.None])!;
            Assert.Equal(1, await Run());
            Assert.Equal(0, await Run());
        }
        Assert.Empty(await db.Candles.ToArrayAsync());
        var saved = await db.HistoricalResearchCandles.ToArrayAsync();
        Assert.Equal(3, saved.Length);
        Assert.All(saved, row => Assert.Equal("UpstoxHistoricalIndexV3", row.Source));
        Assert.All(saved, row => Assert.Equal(start, row.OpenTimeUtc));
    }
}
