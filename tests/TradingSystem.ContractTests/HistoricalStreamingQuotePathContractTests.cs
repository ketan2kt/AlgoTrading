using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class HistoricalStreamingQuotePathContractTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("sarthi-stream-quotes-").FullName;
    private static readonly DateTimeOffset At = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MoreThanOneMillionQuotesCanBeSortedAndReadWithoutTheLegacyObjectCap()
    {
        const int count = 1_000_010;
        var path = await FileHistoricalReplayQuotePath.BuildAsync(directory, GenerateAsync(), CancellationToken.None);
        Assert.Equal(count, path.Count);
        Assert.Equal(At, path[0].AvailableAtUtc);
        Assert.Equal(At.AddMilliseconds(count - 1), path[count - 1].AvailableAtUtc);
        Assert.Empty(Directory.GetDirectories(directory, ".build-*"));

        static async IAsyncEnumerable<HistoricalReplayQuote> GenerateAsync()
        {
            for (var index = 0; index < count; index++) yield return new("NIFTYCE", At.AddMilliseconds(index), 100);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExternalSortAndMultiPassMergeAreRepeatableAndCollapseIdenticalRetries()
    {
        var quotes = Enumerable.Range(0, 1000).Reverse().Select(index =>
            new HistoricalReplayQuote("NIFTYCE", At.AddSeconds(index), 100 + index / 100m)).ToArray();
        var path = await FileHistoricalReplayQuotePath.BuildAsync(directory, AsAsync(quotes.Concat(quotes.Take(3))), CancellationToken.None, 8);
        Assert.Equal(1000, path.Count);
        Assert.Equal(quotes.Reverse(), path);
        Assert.Equal(quotes.Reverse(), path); // Re-readable, no exhausted cursor.
        var restored = await FileHistoricalReplayQuotePath.OpenAsync(directory, path.Reference, CancellationToken.None);
        Assert.Equal(path, restored);
        Assert.Empty(Directory.GetDirectories(directory, ".build-*"));
        await File.AppendAllTextAsync(Assert.Single(Directory.GetFiles(directory, "*.quotes.gz")), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => FileHistoricalReplayQuotePath.OpenAsync(directory, path.Reference, CancellationToken.None));
    }

    [Fact]
    public async Task ConflictingVersionsAndCancelledBuildsDoNotPublishAPath()
    {
        var quote = new HistoricalReplayQuote("NIFTYCE", At, 100);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileHistoricalReplayQuotePath.BuildAsync(directory,
            AsAsync([quote, quote with { LastPrice = 101 }]), CancellationToken.None, 1));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileHistoricalReplayQuotePath.BuildAsync(directory,
            AsAsync([quote]), cancellation.Token));
        Assert.Empty(Directory.GetFiles(directory, "*.quotes.gz"));
        Assert.Empty(Directory.GetDirectories(directory, ".build-*"));
    }

    [Fact]
    public async Task ReplayLoaderBypassesTheOldObservationObjectLimitWithoutDroppingQuotes()
    {
        using var archive = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            directory, maximumReadObservations: 1);
        for (var second = 0; second < 3; second++) archive.TryRecord(new(Guid.NewGuid(), At.AddSeconds(second),
            new("NSE", "FNO", "NIFTYCE"), new(100 + second, null, 99 + second, 65, 101 + second, 65, 1000, 5000, null)));
        await archive.FlushBatchAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.ReadAsync(new(2026, 11, 4), CancellationToken.None));
        var result = await archive.ReadReplayAsync(new(2026, 11, 4), "NIFTY", "NSE",
            new Dictionary<string, string> { ["NIFTYCE"] = "NIFTYCE" }, At, At.AddMinutes(1), CancellationToken.None);
        Assert.IsAssignableFrom<IHistoricalChronologicalQuotePath>(result.Quotes);
        Assert.Equal(3, result.Quotes.Count);
        Assert.False(result.HasCaptureGaps);
    }

    [Fact]
    public async Task DiskAndMemoryPathsProduceExactlyTheSameFillsAndFingerprint()
    {
        var quotes = new[] { new HistoricalReplayQuote("NIFTYCE", At.AddMinutes(2), 100),
            new HistoricalReplayQuote("NIFTYCE", At.AddMinutes(3), 120), new HistoricalReplayQuote("NIFTYCE", At.AddMinutes(4), 115) };
        var disk = await FileHistoricalReplayQuotePath.BuildAsync(directory, AsAsync(quotes.Reverse()), CancellationToken.None, 1);
        var bars = new[] { new HistoricalReplayBar(At, 60, 100, 101, 99, 100) { Symbol = "NIFTY" } };
        HistoricalReplayResult Run(IReadOnlyList<HistoricalReplayQuote> path) => HistoricalPortfolioReplay.Run(bars, path,
            new Entry(), new(.1m, .15m, 0, 0, 10, 0), At, At.AddMinutes(6));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(Run(quotes)), System.Text.Json.JsonSerializer.Serialize(Run(disk)));
    }

    private sealed class Entry : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "stream-equivalence-fixture";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == At.AddMinutes(1) ? [new("NIFTYCE", frame.Market.AvailableAtUtc, "normal", .8m, 65)] : [];
    }

    private static async IAsyncEnumerable<HistoricalReplayQuote> AsAsync(IEnumerable<HistoricalReplayQuote> quotes,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        foreach (var quote in quotes) { token.ThrowIfCancellationRequested(); yield return quote; }
        await Task.CompletedTask;
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
