using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class ReplayQuoteCacheBudgetTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("replay-cache-budget-").FullName;

    [Fact]
    public void EvictsOldDownloadedCopiesButNeverPinnedDaysOrSavedResults()
    {
        var cache = new ReplayQuoteCacheBudget(root, 100);
        var old = Shard("2026-11-04", 45);
        var active = Shard("2026-11-05", 45);
        var result = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "replay-paths")).FullName, "saved.quotes.gz");
        File.WriteAllText(result, "retain saved result");
        var foreign = Path.Combine(root, "not-a-download.bin");
        File.WriteAllText(foreign, "retain");
        using var pin = cache.Pin(new(2026, 11, 5));
        cache.Reserve(30, 1000, 100);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(active));
        Assert.True(File.Exists(result));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void ActiveInputsAreNeverDeletedToPretendAnOversizedRequestFits()
    {
        var cache = new ReplayQuoteCacheBudget(root, 100);
        var active = Shard("2026-11-05", 90);
        using (cache.Pin(new(2026, 11, 5)))
            Assert.Throws<IOException>(() => cache.Reserve(30, 1000, 100));
        Assert.True(File.Exists(active));
        cache.Reserve(30, 1000, 100);
        Assert.False(File.Exists(active));
    }

    [Fact]
    public void NestedReadersHoldTheDayUntilBothHaveFinished()
    {
        var cache = new ReplayQuoteCacheBudget(root, 100);
        var active = Shard("2026-11-05", 90);
        using var first = cache.Pin(new(2026, 11, 5));
        var second = cache.Pin(new(2026, 11, 5));
        second.Dispose(); second.Dispose();
        Assert.Throws<IOException>(() => cache.Reserve(30, 1000, 100));
        Assert.True(File.Exists(active));
    }

    [Fact]
    public void DiskReserveEvictsCopiesEvenBelowTheCacheBudget()
    {
        var cache = new ReplayQuoteCacheBudget(root, 1000);
        var old = Shard("2026-11-04", 90);
        cache.Reserve(20, 50, 100);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void SingleOversizedShardFailsWithoutDeletingExistingCopies()
    {
        var cache = new ReplayQuoteCacheBudget(root, 100);
        var old = Shard("2026-11-04", 10);
        Assert.Throws<IOException>(() => cache.Reserve(101, 1000, 100));
        Assert.True(File.Exists(old));
    }

    private string Shard(string day, int bytes)
    {
        var path = Path.Combine(root, day + "-04-00.ndjson.gz");
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastAccessTimeUtc(path, DateTime.Parse(day, System.Globalization.CultureInfo.InvariantCulture));
        return path;
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
