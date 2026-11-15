using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class HistoricalQuoteArchiveContractTests : IDisposable
{
    [Fact]
    public async Task RestoredArchivePreservesAllMarketsSourcesDepthAndKnownGaps()
    {
        var expected = new List<HistoricalQuoteObservation>();
        using (var writer = Archive(capacity: 100))
        {
            foreach (var (exchange, market, strike) in new[]
                     { ("NSE", "NIFTY", 25000), ("BSE", "SENSEX", 82000), ("NSE", "BANKNIFTY", 61000) })
            foreach (var side in new[] { "CE", "PE" })
            {
                var primary = Observation() with
                {
                    Request = new(exchange, "FNO", $"{market}26NOV{strike}{side}"),
                    Source = "GrowwFeedSample", DepthSourceAtUtc = At
                };
                var backup = primary with
                {
                    Id = Guid.NewGuid(), Source = HistoricalQuoteObservation.UpstoxSnapshot,
                    DepthSourceAtUtc = null, BrokerObservedAtUtc = At, BackupLiquidityAtUtc = At
                };
                writer.TryRecord(primary);
                Assert.True(writer.TryRecordBackup(backup));
                expected.AddRange([primary, backup]);
            }
            writer.MarkCaptureGap(Date);
            await writer.FlushBatchAsync(CancellationToken.None);
        }
        var savedFiles = Directory.GetFiles(directory);
        var restoredRoot = Directory.CreateDirectory(Path.Combine(directory, "isolated-restore")).FullName;
        foreach (var file in savedFiles)
        {
            var restoredFile = Path.Combine(restoredRoot, Path.GetFileName(file));
            File.Copy(file, restoredFile);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file)),
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(restoredFile)));
        }
        using var restored = new HistoricalQuoteArchive(TimeProvider.System,
            NullLogger<HistoricalQuoteArchive>.Instance, restoredRoot);
        var recovered = await restored.ReadAsync(Date, CancellationToken.None);
        Assert.Equal(expected.Where(row => row.Source == "GrowwFeedSample").OrderBy(row => row.Id),
            recovered.Observations.OrderBy(row => row.Id));
        Assert.True(recovered.HasCaptureGaps);
        foreach (var (exchange, market) in new[] { ("NSE", "NIFTY"), ("BSE", "SENSEX"), ("NSE", "BANKNIFTY") })
        {
            var symbols = expected.Where(row => row.Request.TradingSymbol.StartsWith(market, StringComparison.Ordinal))
                .Select(row => row.Request.TradingSymbol).Distinct().ToDictionary(symbol => symbol);
            var strict = await restored.ReadReplayAsync(Date, market, exchange, symbols, At, At.AddMinutes(1),
                CancellationToken.None, streamingOnly: true);
            var assisted = await restored.ReadReplayAsync(Date, market, exchange, symbols, At, At.AddMinutes(1),
                CancellationToken.None, streamingOnly: true, includeBackup: true);
            Assert.Equal(2, strict.Quotes.Count);
            Assert.All(strict.Quotes, row => Assert.Equal("GrowwFeedSample", row.Source));
            Assert.Equal(4, assisted.Quotes.Count);
            Assert.True(assisted.HasCaptureGaps);
            Assert.Equal(expected.Where(row => symbols.ContainsKey(row.Request.TradingSymbol))
                    .Select(row => HistoricalQuoteProjection.ToReplay(row, row.Request.TradingSymbol))
                    .OrderBy(row => row.Symbol).ThenBy(row => row.Source),
                assisted.Quotes.OrderBy(row => row.Symbol).ThenBy(row => row.Source));
            Assert.All(assisted.Quotes, row => { Assert.Equal(100L, row.BidQuantity); Assert.Equal(100L, row.OfferQuantity); });
        }
    }

    [Fact]
    public async Task IndependentWebsiteBackupIsReadableWithoutAnyRecorderShard()
    {
        using var writer = Archive();
        var observation = Observation() with { Source = HistoricalQuoteObservation.UpstoxSnapshot, BrokerObservedAtUtc = At };
        Assert.True(writer.TryRecordBackup(observation));
        await writer.FlushBatchAsync(CancellationToken.None);
        var shard = Assert.Single(Directory.GetFiles(directory, "*.ndjson.gz"));
        File.Move(shard, Path.Combine(directory, Path.GetFileName(ReplayBlobStorage.WebsiteQuotePath("quotes/" + Path.GetFileName(shard)))));
        var symbols = new Dictionary<string, string> { [observation.Request.TradingSymbol] = observation.Request.TradingSymbol };
        var strict = await writer.ReadReplayAsync(Date, "NIFTY", "NSE", symbols, At, At.AddMinutes(1), CancellationToken.None, streamingOnly: true);
        Assert.Empty(strict.Quotes);
        var assisted = await writer.ReadReplayAsync(Date, "NIFTY", "NSE", symbols, At, At.AddMinutes(1), CancellationToken.None, streamingOnly: true, includeBackup: true);
        Assert.Equal(HistoricalQuoteObservation.UpstoxSnapshot, Assert.Single(assisted.Quotes).Source);
    }

    [Fact]
    public async Task SupplementalQuotesNeverEnterStrictReplayOrPaperTimeline()
    {
        using var archive = Archive();
        var observation = Observation();
        Assert.True(archive.TryRecordBackup(observation with { Source = HistoricalQuoteObservation.UpstoxSnapshot,
            BrokerObservedAtUtc = At, BackupLiquidityAtUtc = At.AddSeconds(-5) }));
        archive.SetBackupCoverage(Date, new Dictionary<string, HistoricalBackupMarketCoverage>
            { ["NIFTY"] = new(2, 1, ["NIFTYMISSINGPE"]) });
        archive.SetBackupFailure(Date, "SENSEX", "HTTP 429: broker retry delay");
        Assert.False(archive.TryRecordBackup(observation with { Id = Guid.NewGuid(), Source = HistoricalQuoteObservation.UpstoxSnapshot,
            BrokerObservedAtUtc = At.AddSeconds(1) })); // future timestamps never enter the archive
        await archive.FlushBatchAsync(CancellationToken.None);
        var symbols = new Dictionary<string, string> { [observation.Request.TradingSymbol] = observation.Request.TradingSymbol };
        var strict = await archive.ReadReplayAsync(Date, "NIFTY", "NSE", symbols, At, At.AddSeconds(2), CancellationToken.None, streamingOnly: true);
        Assert.Empty(strict.Quotes);
        var modeled = await archive.ReadReplayAsync(Date, "NIFTY", "NSE", symbols, At, At.AddSeconds(2), CancellationToken.None, streamingOnly: true, includeBackup: true);
        Assert.Equal(1, modeled.BackupSamples);
        Assert.Equal(HistoricalQuoteObservation.UpstoxSnapshot, Assert.Single(modeled.Quotes).Source);
        Assert.Null(modeled.Quotes[0].DepthSourceAtUtc);
        Assert.Equal(At.AddSeconds(-5), modeled.Quotes[0].BackupLiquidityAtUtc);
        var published = System.Text.Json.JsonSerializer.Deserialize<HistoricalQuoteArchiveHealth>(
            System.Text.Json.JsonSerializer.Serialize(archive.ReadHealth(Date)))!;
        Assert.Equal("NIFTYMISSINGPE", Assert.Single(published.UpstoxCoverage["NIFTY"].MissingContracts));
        Assert.Equal("HTTP 429: broker retry delay", published.UpstoxFailures["SENSEX"]);
        Assert.Equal(At, archive.ReadHealth(Date).LastUpstoxOptionWrittenAtByMarket["NIFTY"]);
        Assert.Empty(archive.ReadHealth(Date).LastOptionWrittenAtByMarket);
        Assert.Empty((await archive.ReadAsync(Date, CancellationToken.None)).Observations);
        Assert.Empty((await archive.HeldMarksAsync(Date,
            [(Guid.NewGuid(), "NSE", observation.Request.TradingSymbol, At, At.AddSeconds(2))], CancellationToken.None)).Marks);
    }
    [Fact]
    public async Task GapAwareReadMarksCorruptMinuteAndKeepsLaterQuotesWithoutRelaxingStrictRead()
    {
        using var archive = Archive();
        var sample = Observation() with { ReceivedAtUtc = At.AddMinutes(1), Source = "GrowwFeedSample" };
        archive.TryRecord(sample);
        await archive.FlushBatchAsync(CancellationToken.None);
        var corrupt = Path.Combine(directory, "2026-11-04-04-00.ndjson.gz");
        await File.WriteAllTextAsync(corrupt, "invalid gzip member");
        var symbols = new Dictionary<string, string> { [sample.Request.TradingSymbol] = sample.Request.TradingSymbol };
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.ReadReplayAsync(Date, "NIFTY", "NSE", symbols,
            At, At.AddMinutes(2), CancellationToken.None, streamingOnly: true));
        var read = await archive.ReadReplayAsync(Date, "NIFTY", "NSE", symbols, At, At.AddMinutes(2),
            CancellationToken.None, streamingOnly: true, tolerateUnreadableShards: true);
        Assert.True(read.HasCaptureGaps);
        Assert.Equal(sample.ReceivedAtUtc, Assert.Single(read.Quotes).AvailableAtUtc);
        Assert.Equal(new HistoricalReplayGap(At, At.AddMinutes(1), "Unreadable archive minute"), Assert.Single(read.UnreadableIntervals));
    }
    [Fact]
    public async Task MirroredWebsiteReceiptsSupportTimelinesButDoNotChangeStreamingCoverageOrFills()
    {
        var cloud = Path.Combine(directory, "remote");
        var archivePath = Path.Combine(cloud, "quotes");
        var websitePath = Path.Combine(directory, "website");
        using var recorder = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance, archivePath);
        using var website = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance, websitePath);
        var sample = Observation() with { Source = "GrowwFeedSample", DepthSourceAtUtc = At };
        var ordinary = Observation() with { ReceivedAtUtc = At.AddSeconds(1) };
        var fallback = Observation() with { ReceivedAtUtc = At.AddSeconds(2), Source = HistoricalQuoteObservation.GrowwRestFallback };
        recorder.TryRecord(sample);
        website.TryRecord(ordinary);
        website.TryRecord(fallback);
        await recorder.FlushBatchAsync(CancellationToken.None);
        await website.FlushBatchAsync(CancellationToken.None);
        var originalFile = Assert.Single(Directory.GetFiles(websitePath, "*.ndjson.gz"));
        var mirror = ReplayBlobStorage.WebsiteQuotePath("quotes/" + Path.GetFileName(originalFile));
        Assert.True(ReplayBlobStorage.Allowed(mirror));
        File.Copy(originalFile, Path.Combine(cloud, mirror));
        await File.WriteAllTextAsync(Path.Combine(archivePath, "2026-11-04-website.capture-gap"), "website receipt gap");
        using var reader = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            websitePath, cloud: new CachedArchive(cloud));
        var symbols = new Dictionary<string, string> { [sample.Request.TradingSymbol] = sample.Request.TradingSymbol };
        var verified = await reader.ReadReplayAsync(Date, "NIFTY", "NSE", symbols, At, At.AddMinutes(1), CancellationToken.None, streamingOnly: true);
        Assert.Equal("GrowwFeedSample", Assert.Single(verified.Quotes).Source);
        Assert.False(verified.HasCaptureGaps);
        var ordinaryReplay = await reader.ReadReplayAsync(Date, "NIFTY", "NSE", symbols, At, At.AddMinutes(1), CancellationToken.None);
        Assert.Equal(2, ordinaryReplay.Quotes.Count);
        Assert.DoesNotContain(ordinaryReplay.Quotes, quote => quote.Source == HistoricalQuoteObservation.GrowwRestFallback);
        var id = Guid.NewGuid();
        var (marks, _) = await reader.HeldMarksAsync(Date, [(id, "NSE", sample.Request.TradingSymbol, At, At.AddMinutes(1))], CancellationToken.None);
        Assert.Equal(3, marks[id].Count);
        Assert.Contains(marks[id], mark => mark.Source == HistoricalQuoteObservation.GrowwRestFallback && mark.At == fallback.ReceivedAtUtc);
    }
    [Theory]
    [InlineData("NSE", "NIFTY26110322500CE", "NIFTY")]
    [InlineData("BSE", "SENSEX26NOV75000PE", "SENSEX")]
    [InlineData("NSE", "BANKNIFTY26NOV54000CE", "BANKNIFTY")]
    public async Task RecorderOnlyQuotesRemainVisibleToTimelineAndMarketReaders(string exchange, string symbol, string market)
    {
        var remoteRoot = Path.Combine(directory, "remote");
        using var recorder = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            Path.Combine(remoteRoot, "quotes"));
        var sample = Observation() with { Request = new(exchange, "FNO", symbol), Source = "GrowwFeedSample" };
        recorder.TryRecord(sample);
        await recorder.FlushBatchAsync(CancellationToken.None);
        using var website = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            Path.Combine(directory, "empty-website"), cloud: new CachedArchive(remoteRoot));
        Assert.Equal(sample, Assert.Single((await website.ReadMarketAsync(Date, market, CancellationToken.None)).Observations));
        var id = Guid.NewGuid();
        var (marks, gaps) = await website.HeldMarksAsync(Date, [(id, exchange, symbol, At, At.AddMinutes(1))], CancellationToken.None);
        Assert.Single(marks[id]);
        Assert.False(gaps);
    }

    [Fact]
    public async Task EmptyCloudDoesNotHideLocalStreamingHistory()
    {
        using var writer = Archive();
        var sample = Observation() with { Source = "GrowwFeedSample" };
        writer.TryRecord(sample);
        await writer.FlushBatchAsync(CancellationToken.None);
        using var reader = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            directory, cloud: new CachedArchive(Path.Combine(directory, "empty-cloud")));
        var path = await reader.ReadReplayAsync(Date, "NIFTY", "NSE",
            new Dictionary<string, string> { [sample.Request.TradingSymbol] = sample.Request.TradingSymbol },
            At, At.AddMinutes(1), CancellationToken.None, streamingOnly: true);
        Assert.Single(path.Quotes);
        Assert.False(path.HasCaptureGaps);
    }

    [Fact]
    public async Task MirroredObservationIsNotDoubleCountedInTimeline()
    {
        var remoteRoot = Path.Combine(directory, "remote");
        var sample = Observation();
        using var recorder = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            Path.Combine(remoteRoot, "quotes"));
        using var website = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            directory, cloud: new CachedArchive(remoteRoot));
        recorder.TryRecord(sample);
        website.TryRecord(sample);
        await recorder.FlushBatchAsync(CancellationToken.None);
        await website.FlushBatchAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        var (marks, gaps) = await website.HeldMarksAsync(Date,
            [(id, "NSE", sample.Request.TradingSymbol, At, At.AddMinutes(1))], CancellationToken.None);
        Assert.Single(marks[id]);
        Assert.False(gaps);
        Assert.Single((await website.ReadAsync(Date, CancellationToken.None)).Observations);
    }

    private sealed class CachedArchive(string path) : IReplayArchiveCache
    {
        public bool Enabled => true;
        public string CacheRoot => path;
        public Task HydrateAsync(string category, DateOnly? tradingDate, CancellationToken token) => Task.CompletedTask;
    }
    [Fact]
    public async Task FeedPathFiltersRestBeforeSortingSoSameTimeRestCannotEraseDepthOrBreakReplay()
    {
        using var archive = Archive();
        var observation = Observation();
        archive.TryRecord(observation with { Source = "GrowwFeedSample", DepthSourceAtUtc = At });
        archive.TryRecord(observation with { Id = Guid.NewGuid(), Quote = observation.Quote with { BidPrice = null, OfferPrice = null } });
        await archive.FlushBatchAsync(CancellationToken.None);
        var path = await archive.ReadReplayAsync(Date, "NIFTY", "NSE",
            new Dictionary<string,string> { [observation.Request.TradingSymbol] = observation.Request.TradingSymbol },
            At, At.AddMinutes(1), CancellationToken.None, streamingOnly: true);
        var quote = Assert.Single(path.Quotes);
        Assert.Equal(99, quote.BidPrice);
        Assert.Equal("GrowwFeedSample", quote.Source);
        Assert.False(path.HasCaptureGaps);
    }
    private readonly string directory = Directory.CreateTempSubdirectory("sarthi-quote-archive-").FullName;
    private static readonly DateOnly Date = new(2026, 11, 4);
    private static readonly DateTimeOffset At = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("NSE","NIFTY26NOV23000CE")]
    [InlineData("BSE","SENSEX26NOV75000PE")]
    [InlineData("NSE","BANKNIFTY26NOV54000CE")]
    public async Task TimelineReadsOnlyHeldOptionReceiptsWithoutRetimingThePrice(string exchange,string symbol)
    {
        using var archive=Archive();
        var id=Guid.NewGuid();
        var sample=Observation() with { Request=new(exchange,"FNO",symbol),ReceivedAtUtc=At,
            Quote=Observation().Quote with {LastTradeTimeEpochMilliseconds=At.AddSeconds(-45).ToUnixTimeMilliseconds()} };
        archive.TryRecord(sample);
        archive.TryRecord(sample with {Id=Guid.NewGuid(),ReceivedAtUtc=At.AddMinutes(1)});
        archive.TryRecord(sample with {Id=Guid.NewGuid(),Request=new(exchange,"CASH",symbol)});
        await archive.FlushBatchAsync(CancellationToken.None);
        var (marks,gaps)=await archive.HeldMarksAsync(Date,[(id,exchange,symbol,At,At.AddMinutes(1))],CancellationToken.None);
        Assert.False(gaps);
        var point=Assert.Single(marks[id]);
        Assert.Equal(At,point.At);
        Assert.Equal(At.AddSeconds(-45),point.SourceAt);
    }

    [Fact]
    public async Task ExistingQuoteResponsePreservesTwoSidedPriceOiAndReceiptTimeAcrossRestart()
    {
        using var archive = Archive();
        var observation = Observation();
        archive.TryRecord(observation);
        await archive.FlushBatchAsync(CancellationToken.None);
        using var restarted = Archive();
        var restored = await restarted.ReadAsync(Date, CancellationToken.None);
        Assert.Equal(observation, Assert.Single(restored.Observations));
        Assert.False(restored.HasCaptureGaps);
    }

    [Fact]
    public async Task FullBufferDoesNotBlockCallerAndItsLossIsPersisted()
    {
        using var archive = Archive(1);
        archive.TryRecord(Observation());
        archive.TryRecord(Observation());
        await archive.FlushBatchAsync(CancellationToken.None);
        using var restarted = Archive();
        var restored = await restarted.ReadAsync(Date, CancellationToken.None);
        Assert.Single(restored.Observations);
        Assert.True(restored.HasCaptureGaps);
    }

    [Fact]
    public async Task CorruptLineDoesNotHideLaterObservationsOrPretendCompleteCapture()
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "2026-11-04.ndjson"), "interrupted-write");
        using var archive = Archive();
        archive.TryRecord(Observation());
        await archive.FlushBatchAsync(CancellationToken.None);
        var restored = await archive.ReadAsync(Date, CancellationToken.None);
        Assert.Single(restored.Observations);
        Assert.True(restored.HasCaptureGaps);
    }

    [Fact]
    public async Task GracefulStopDrainsBufferAndDoesNotCaptureNaturalGasOrBankex()
    {
        using var archive = Archive();
        archive.TryRecord(Observation());
        archive.TryRecord(Observation() with { Request = new("MCX", "COMMODITY", "NATURALGAS") });
        archive.TryRecord(Observation() with { Request = new("BSE", "FNO", "BANKEX") });
        await archive.StopAsync(CancellationToken.None);
        Assert.Single((await archive.ReadAsync(Date, CancellationToken.None)).Observations);
    }

    [Fact]
    public async Task MarketReadDoesNotRetainOtherMarketsAndFailsRatherThanTruncateAtItsMemoryBudget()
    {
        using var archive = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            directory, maximumReadObservations: 1);
        archive.TryRecord(Observation());
        archive.TryRecord(Observation() with { Request = new("BSE", "FNO", "SENSEXCE") });
        await archive.FlushBatchAsync(CancellationToken.None);
        Assert.Single((await archive.ReadMarketAsync(Date, "NIFTY", CancellationToken.None)).Observations);
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.ReadAsync(Date, CancellationToken.None));
    }

    [Fact]
    public async Task StorageBudgetPreservesOldFilesAndPersistsAnExplicitGap()
    {
        using var archive = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            directory, maximumArchiveBytes: 0);
        archive.TryRecord(Observation());
        await archive.FlushBatchAsync(CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(directory, "2026-11-04.capture-gap")));
        // No fabricated successful sample or silent deletion when space is exhausted.
        Assert.Empty(Directory.GetFiles(directory, "*.gz"));
    }

    [Fact]
    public async Task CompressedBatchesAndMinuteShardsRoundTripWithoutLosingEarlierMembers()
    {
        using var archive = Archive();
        archive.TryRecord(Observation());
        await archive.FlushBatchAsync(CancellationToken.None);
        archive.TryRecord(Observation() with { ReceivedAtUtc = At.AddSeconds(3) });
        await archive.FlushBatchAsync(CancellationToken.None);
        archive.TryRecord(Observation() with { ReceivedAtUtc = At.AddMinutes(1) });
        archive.TryRecord(Observation() with { ReceivedAtUtc = At.AddMinutes(2) });
        await archive.FlushBatchAsync(CancellationToken.None);
        using var restarted = Archive();
        var result = await restarted.ReadAsync(Date, CancellationToken.None);
        Assert.Equal(4, result.Observations.Count);
        Assert.False(result.HasCaptureGaps);
        Assert.Equal(3, Directory.GetFiles(directory, "*.gz").Length);
        Assert.True(File.Exists(Path.Combine(directory, "2026-11-04-04-01.ndjson.gz")));
    }

    [Fact]
    public async Task InterruptedShardWriteKeepsCommittedBytesAndRetriesOnlyTheUncommittedObservations()
    {
        using var archive = Archive();
        var original = Observation();
        var earlier = Observation() with { ReceivedAtUtc = At.AddSeconds(-1) };
        var next = Observation() with { ReceivedAtUtc = At.AddSeconds(2) };
        archive.TryRecord(original);
        await archive.FlushBatchAsync(CancellationToken.None);
        var shard = Path.Combine(directory, "2026-11-04-04-00.ndjson.gz");
        var committed = await File.ReadAllBytesAsync(shard);
        var staging = shard + ".pending";
        Directory.CreateDirectory(staging);
        archive.TryRecord(earlier);
        archive.TryRecord(next);
        var failure = await Record.ExceptionAsync(() => archive.FlushBatchAsync(CancellationToken.None));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(committed, await File.ReadAllBytesAsync(shard));
        Assert.Equal(1, archive.ReadHealth(Date).Buffered);
        Directory.Delete(staging);
        // Simulate a leftover, truncated staging file after an abrupt process stop.
        await File.WriteAllBytesAsync(staging, [0x1f, 0x8b, 0x08]);
        Assert.Equal(2, (await archive.ReadAsync(Date, CancellationToken.None)).Observations.Count);
        await archive.FlushBatchAsync(CancellationToken.None);
        var result = await archive.ReadAsync(Date, CancellationToken.None);
        Assert.Equal(new[] { earlier.Id, original.Id, next.Id }.Order(), result.Observations.Select(item => item.Id).Order());
        Assert.False(result.HasCaptureGaps);
        Assert.False(File.Exists(staging));
    }

    private HistoricalQuoteArchive Archive(int capacity = 10) => new(TimeProvider.System,
        NullLogger<HistoricalQuoteArchive>.Instance, directory, capacity);
    private static HistoricalQuoteObservation Observation() => new(Guid.NewGuid(), At,
        new("NSE", "FNO", "NIFTY26110322500CE"), new(100, At.ToUnixTimeMilliseconds(), 99, 100, 101, 100, 1000, 5000, 10));
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
