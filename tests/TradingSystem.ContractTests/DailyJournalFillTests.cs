using Microsoft.EntityFrameworkCore;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Diagnostics;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.ContractTests;

public sealed class DailyJournalFillTests
{
    [Fact]
    public void JournalFallbackUsesActualFirstEntryFillNotSignalSubmissionExitOrOtherTrades()
    {
        var signal = DateTimeOffset.UnixEpoch;
        var rows = new[] {
            new PaperBrokerEvent(Guid.NewGuid(), 1, "OrderSubmitted", "a-ENTRY", "{}", signal),
            new PaperBrokerEvent(Guid.NewGuid(), 2, "OrderFilled", "a-ENTRY", "{}", signal.AddSeconds(15)),
            new PaperBrokerEvent(Guid.NewGuid(), 3, "OrderFilled", "a-ENTRY", "{}", signal.AddSeconds(20)),
            new PaperBrokerEvent(Guid.NewGuid(), 4, "OrderFilled", "a-EXIT", "{}", signal.AddSeconds(40)),
            new PaperBrokerEvent(Guid.NewGuid(), 5, "OrderFilled", "b-ENTRY", "{}", signal.AddSeconds(1)) };
        var receipt = Assert.Single(EfDailyTradingDiagnosticsReader.JournalEntryFills(rows.AsQueryable(), ["a-ENTRY"]));
        Assert.Equal("a-ENTRY", receipt.ClientReference);
        Assert.Equal(signal.AddSeconds(15), receipt.At);
        Assert.Empty(EfDailyTradingDiagnosticsReader.JournalEntryFills(rows.AsQueryable(), ["missing-ENTRY"]));
    }

    [Fact]
    public void JournalFillLookupTranslatesToBoundedPostgresGroupingWithoutAConnection()
    {
        using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options, TimeProvider.System);
        var sql = EfDailyTradingDiagnosticsReader.JournalEntryFills(db.PaperBrokerEvents, ["a-ENTRY"]).ToQueryString();
        Assert.Contains("min(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OrderFilled", sql, StringComparison.Ordinal);
    }
}
