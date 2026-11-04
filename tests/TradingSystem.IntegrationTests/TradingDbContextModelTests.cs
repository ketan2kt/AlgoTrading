using Microsoft.EntityFrameworkCore;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.IntegrationTests;

public sealed class TradingDbContextModelTests
{
    [Fact]
    public void ModelContainsModeScopedAndIdempotencyConstraints()
    {
        using var context = CreateContext();
        var settings = context.Model.FindEntityType(typeof(ApplicationSetting));
        var orders = context.Model.FindEntityType(typeof(TradingOrder));
        var signals = context.Model.FindEntityType(typeof(Signal));
        var priceSamples = context.Model.FindEntityType(typeof(PaperTradePriceSample));

        Assert.NotNull(settings);
        Assert.Contains(
            settings.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual(["Mode", "Key"]));
        Assert.NotNull(orders);
        Assert.Contains(
            orders.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual(["Mode", "ClientReference"]));
        Assert.NotNull(signals);
        Assert.Contains(
            signals.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Single().Name == "Fingerprint");
        Assert.NotNull(priceSamples);
        Assert.Contains(priceSamples.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["SignalId", "ObservedBucketUtc"]));
    }

    [Fact]
    public async Task AppendOnlyEntityCannotBeModified()
    {
        await using var context = CreateContext();
        var audit = new AuditLog(
            Guid.NewGuid(),
            "operator",
            "ConfigurationChanged",
            "ApplicationSetting",
            Guid.NewGuid().ToString(),
            "test",
            "{}",
            "{}",
            Guid.NewGuid().ToString(),
            DateTimeOffset.UtcNow);
        context.Attach(audit);
        context.Entry(audit).State = EntityState.Modified;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public void HistoricalResearchCandlesHaveUniqueProviderMinuteAndAppendOnlyMapping()
    {
        using var context = CreateContext();
        var archived = context.Model.FindEntityType(typeof(HistoricalResearchCandle));

        Assert.NotNull(archived);
        Assert.Equal("historical_research_candles", archived.GetTableName());
        Assert.Contains(archived.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                "GrowwSymbol", "OpenTimeUtc", "IntervalSeconds", "Source"
            ]));
        Assert.Equal(24, archived.FindProperty("OpenInterest")?.GetPrecision());
    }

    [Fact]
    public async Task HistoricalResearchCandleCannotBeRewritten()
    {
        await using var context = CreateContext();
        context.Attach(new HistoricalResearchCandle(Guid.NewGuid(), "NSE", "NIFTY",
            "NSE-NIFTY-29Oct26-22000-CE", new DateOnly(2026, 10, 29),
            new DateTimeOffset(2026, 10, 29, 4, 0, 0, TimeSpan.Zero),
            60, 100m, 101m, 99m, 100m, 10, 1000m,
            "GrowwHistoricalApiV1"));
        context.ChangeTracker.Entries<HistoricalResearchCandle>().Single().State =
            EntityState.Modified;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public void HistoricalUniversePageReceiptHasUniqueAppendOnlyPageIdentity()
    {
        using var context = CreateContext();
        var receipt = context.Model.FindEntityType(typeof(HistoricalUniversePageReceipt));
        Assert.NotNull(receipt);
        Assert.Equal("historical_universe_page_receipts", receipt.GetTableName());
        Assert.Contains(receipt.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                "SessionDate", "Market", "ExpiryDate", "Page", "PageSize"
            ]));
    }

    [Fact]
    public async Task HistoricalUniversePageReceiptCannotBeRewritten()
    {
        await using var context = CreateContext();
        context.Attach(new HistoricalUniversePageReceipt(Guid.NewGuid(),
            new DateOnly(2026, 10, 29), "nifty", new DateOnly(2026, 11, 3),
            0, 20, 2, new string('A', 64), "[\"CE\",\"PE\"]",
            DateTimeOffset.UtcNow));
        context.ChangeTracker.Entries<HistoricalUniversePageReceipt>()
            .Single().State = EntityState.Modified;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DatedInstrumentMasterSnapshotIsUniqueAndAppendOnly()
    {
        await using var context = CreateContext();
        var snapshot = context.Model.FindEntityType(typeof(HistoricalInstrumentMasterSnapshot));
        Assert.NotNull(snapshot);
        Assert.Equal("historical_instrument_master_snapshots", snapshot.GetTableName());
        Assert.Contains(snapshot.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                "SessionDate", "Source", "Fingerprint"
            ]));

        context.Attach(new HistoricalInstrumentMasterSnapshot(Guid.NewGuid(),
            new DateOnly(2026, 11, 2), DateTimeOffset.UtcNow,
            "GrowwInstrumentMasterV1", new string('A', 64), 1,
            "[{\"tradingSymbol\":\"NIFTY\"}]"));
        context.ChangeTracker.Entries<HistoricalInstrumentMasterSnapshot>()
            .Single().State = EntityState.Modified;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesAsync(CancellationToken.None));
    }

    private static TradingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model_only")
            .Options;
        return new TradingDbContext(options, TimeProvider.System);
    }
}
