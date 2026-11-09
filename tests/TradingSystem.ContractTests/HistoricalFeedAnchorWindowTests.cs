using Microsoft.EntityFrameworkCore;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.ContractTests;

public sealed class HistoricalFeedAnchorWindowTests
{
    [Theory]
    [InlineData(2026, 11, 9)]
    [InlineData(2026, 11, 1)]
    [InlineData(2027, 1, 1)]
    public void AnchorQueryUsesUtcWithoutChangingTheIstPreOpenWindow(int year, int month, int day)
    {
        var date = new DateOnly(year, month, day);
        var (oldest, before) = GrowwHistoricalFeedCaptureService.AnchorWindowUtc(date);
        Assert.Equal(TimeSpan.Zero, oldest.Offset);
        Assert.Equal(TimeSpan.Zero, before.Offset);
        Assert.Equal(date.AddDays(-7).ToDateTime(TimeOnly.MinValue), oldest.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        Assert.Equal(date.ToDateTime(new TimeOnly(9, 15)), before.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        Assert.Equal(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(555), before - oldest);

        // Actual PostgreSQL provider translation, not an in-memory timestamp
        // comparison. No network/database is needed to validate the SQL literals.
        using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options, TimeProvider.System);
        var id = Guid.NewGuid();
        var sql = db.Candles.AsNoTracking().Where(value => value.InstrumentId == id &&
            value.IntervalSeconds == 60 && value.Source == "Groww" &&
            value.OpenTimeUtc >= oldest && value.OpenTimeUtc < before)
            .OrderByDescending(value => value.OpenTimeUtc).Select(value => (decimal?)value.Close)
            .Take(1).ToQueryString();
        Assert.Contains("+00:00", sql, StringComparison.Ordinal);
    }
}
