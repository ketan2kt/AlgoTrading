using System.Text.Json;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.Infrastructure.MarketData;

// Archive existing successful quote responses without extra broker requests or
// disk/database work in the quote caller. Capture gaps invalidate coverage;
// requested-contract quotes do not imply a complete counterfactual universe.
internal sealed class HistoricalQuoteArchive(TimeProvider clock, ILogger<HistoricalQuoteArchive> logger,
    string? directory = null, int capacity = 12500, int maximumReadObservations = 1_000_000,
    long maximumArchiveBytes = 5L * 1024 * 1024 * 1024, IReplayArchiveCache? cloud = null) : BackgroundService, IHistoricalQuoteArchive
{
    private readonly string root = directory ?? Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory,
        "data", "historical-replay", "quotes");
    private readonly Channel<HistoricalQuoteObservation> queue = Channel.CreateBounded<HistoricalQuoteObservation>(
        new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<DateOnly, long> dropped = new();
    private readonly ConcurrentDictionary<DateOnly, long> buffered = new();
    private readonly ConcurrentDictionary<DateOnly, bool> knownGaps = new();
    private readonly ConcurrentDictionary<DateOnly, DateTimeOffset> persisted = new();
    private readonly ConcurrentDictionary<(DateOnly Date, string Market), DateTimeOffset> optionWritten = new();
    private readonly ConcurrentDictionary<(DateOnly Date, string Market), DateTimeOffset> optionSource = new();
    private string? failure;
    private readonly List<HistoricalQuoteObservation> pending = [];
    private DateTimeOffset storageCheckedAt = DateTimeOffset.MinValue;
    private bool storageAvailable;
    private long archiveBytes;
    private long diskFreeBytes = -1;
    private DateTimeOffset lastResourceSample = DateTimeOffset.MinValue;
    private static readonly Action<ILogger, long, long, double, Exception?> ResourceSample = LoggerMessage.Define<long, long, double>(
        LogLevel.Information, new EventId(6754, "ReplayCaptureResources"),
        "Replay quote archive {ArchiveBytes} bytes; host working set {WorkingSetBytes} bytes; oldest receipt in write batch {ReceiptAgeSeconds}s.");
    private static readonly Action<ILogger, Exception?> WriteFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(6752, "HistoricalQuoteArchiveWriteFailed"),
        "Historical quote archive write failed; buffered observations will be retried. Research coverage is not certified.");

    public void TryRecord(HistoricalQuoteObservation observation)
    {
        if (observation.Request.Exchange is not ("NSE" or "BSE") ||
            !SupportedSymbol(observation.Request.TradingSymbol)) return;
        var date = LocalDate(observation.ReceivedAtUtc);
        buffered.AddOrUpdate(date, 1, static (_, count) => count + 1);
        if (!queue.Writer.TryWrite(observation))
        {
            buffered.AddOrUpdate(date, 0, static (_, count) => Math.Max(0, count - 1));
            dropped.AddOrUpdate(date, 1, static (_, count) => count + 1);
            knownGaps[date] = true;
            Volatile.Write(ref failure, "Quote archive queue overflow");
        }
    }

    public void MarkCaptureGap(DateOnly tradingDate)
    {
        dropped.AddOrUpdate(tradingDate, 1, static (_, count) => count + 1);
        knownGaps[tradingDate] = true;
    }

    public HistoricalQuoteArchiveHealth ReadHealth(DateOnly tradingDate) => new(
        buffered.GetValueOrDefault(tradingDate), knownGaps.GetValueOrDefault(tradingDate),
        persisted.TryGetValue(tradingDate, out var at) ? at : null, Volatile.Read(ref failure))
    {
        ArchiveBytes = Interlocked.Read(ref archiveBytes), ArchiveLimitBytes = maximumArchiveBytes,
        DiskFreeBytes = Interlocked.Read(ref diskFreeBytes) is >= 0 and var free ? free : null,
        LastOptionWrittenAtByMarket = optionWritten.Where(value => value.Key.Date == tradingDate)
            .ToDictionary(value => value.Key.Market, value => value.Value),
        LastOptionSourceAtByMarket = optionSource.Where(value => value.Key.Date == tradingDate)
            .ToDictionary(value => value.Key.Market, value => value.Value)
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (pending.Count == 0 && !dropped.Any(value => value.Value > 0))
                {
                    using var wakeup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    wakeup.CancelAfter(TimeSpan.FromSeconds(5));
                    try { if (!await queue.Reader.WaitToReadAsync(wakeup.Token)) break; }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { continue; }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, stoppingToken);
                await FlushBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                Volatile.Write(ref failure, "Quote archive write failed: " + exception.GetType().Name);
                WriteFailed(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken);
            }
        }
    }

    internal async Task FlushBatchAsync(CancellationToken token)
    {
        while (pending.Count < 512 && queue.Reader.TryRead(out var observation)) pending.Add(observation);
        Directory.CreateDirectory(root);
        if (clock.GetUtcNow() - storageCheckedAt >= TimeSpan.FromSeconds(30))
        {
            var bytes = Directory.EnumerateFiles(root, "*.ndjson*").Sum(path => new FileInfo(path).Length);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            Interlocked.Exchange(ref archiveBytes, bytes);
            Interlocked.Exchange(ref diskFreeBytes, drive.IsReady ? drive.AvailableFreeSpace : -1);
            storageAvailable = bytes < maximumArchiveBytes && (!drive.IsReady || drive.AvailableFreeSpace >= 512L * 1024 * 1024);
            storageCheckedAt = clock.GetUtcNow();
            if (pending.Count > 0 && storageCheckedAt - lastResourceSample >= TimeSpan.FromMinutes(5))
            {
                lastResourceSample = storageCheckedAt;
                ResourceSample(logger, bytes, Environment.WorkingSet,
                    Math.Max(0, (storageCheckedAt - pending.Min(item => item.ReceivedAtUtc)).TotalSeconds), null);
            }
        }
        var gaps = dropped.Where(value => value.Value > 0).ToArray();
        var dates = pending.Select(value => LocalDate(value.ReceivedAtUtc)).Concat(gaps.Select(value => value.Key)).Distinct().ToArray();
        foreach (var date in dates)
        {
            var loss = gaps.FirstOrDefault(value => value.Key == date).Value;
            if (!storageAvailable)
            {
                knownGaps[date] = true;
                Volatile.Write(ref failure, "Quote archive storage limit / insufficient disk space");
                // Preserve existing history; never fill the application disk
                // or retry an unlimited research backlog into the live host.
                await File.WriteAllTextAsync(Path.Combine(root, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".capture-gap"),
                    "Research quote storage capacity reached.", token);
                CompleteObservations(date, pending.RemoveAll(value => LocalDate(value.ReceivedAtUtc) == date));
                CompleteGap(date, loss);
                continue;
            }
            // Partition by receipt minute, including batches spanning a boundary.
            // A full 1,000-instrument session must not create hourly members
            // exceeding the reader's compressed/decompressed shard safety limits.
            var shards = pending.Where(value => LocalDate(value.ReceivedAtUtc) == date)
                .GroupBy(value => value.ReceivedAtUtc.UtcTicks / TimeSpan.TicksPerMinute).ToArray();
            foreach (var shard in shards)
            {
                var minute = shard.First().ReceivedAtUtc.UtcDateTime.ToString("HH-mm", CultureInfo.InvariantCulture);
                await CommitMemberAsync(ShardPath(date, minute),
                    shard.Select(value => JsonSerializer.Serialize(new ArchiveLine(value, 0))), token);
                // A failed later shard must not replay this already committed batch.
                CompleteObservations(date, pending.RemoveAll(value => value.ReceivedAtUtc.UtcTicks / TimeSpan.TicksPerMinute == shard.Key));
                persisted[date] = clock.GetUtcNow();
                foreach (var value in shard) RecordOptionProgress(date, value);
            }
            if (loss > 0)
            {
                await CommitMemberAsync(ShardPath(date, "gaps"), [JsonSerializer.Serialize(new ArchiveLine(null, loss))], token);
                CompleteGap(date, loss);
            }
            Volatile.Write(ref failure, null);
        }
    }

    private string ShardPath(DateOnly date, string shard) => Path.Combine(root,
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + shard + ".ndjson.gz");

    private void CompleteGap(DateOnly date, long loss)
    {
        if (loss > 0) dropped.AddOrUpdate(date, 0, (_, count) => Math.Max(0, count - loss));
    }

    private void CompleteObservations(DateOnly date, int count)
    {
        if (count > 0) buffered.AddOrUpdate(date, 0, (_, previous) => Math.Max(0, previous - count));
    }

    private void RecordOptionProgress(DateOnly date, HistoricalQuoteObservation value)
    {
        var symbol = value.Request.TradingSymbol;
        if (value.Source != "GrowwFeedSample" ||
            !(symbol.EndsWith("CE", StringComparison.Ordinal) || symbol.EndsWith("PE", StringComparison.Ordinal))) return;
        var market = symbol.StartsWith("BANKNIFTY", StringComparison.Ordinal) ? "BANKNIFTY" :
            symbol.StartsWith("SENSEX", StringComparison.Ordinal) ? "SENSEX" : "NIFTY";
        var key = (date, market);
        var priceAt = GrowwQuoteTimestamp.ToUtc(value.Quote.LastTradeTimeEpochMilliseconds);
        if (priceAt is { } price && value.DepthSourceAtUtc is { } depth &&
            price <= value.ReceivedAtUtc && depth <= value.ReceivedAtUtc &&
            value.ReceivedAtUtc - price <= TimeSpan.FromSeconds(30) &&
            value.ReceivedAtUtc - depth <= TimeSpan.FromSeconds(30) &&
            value.Quote.LastPrice > 0m && value.Quote.BidPrice > 0m && value.Quote.OfferPrice >= value.Quote.BidPrice &&
            value.Quote.BidQuantity > 0 && value.Quote.OfferQuantity > 0)
        {
            var sourceAt = price < depth ? price : depth;
            optionSource.AddOrUpdate(key, sourceAt, (_, previous) => sourceAt > previous ? sourceAt : previous);
            optionWritten.AddOrUpdate(key, value.ReceivedAtUtc,
                (_, previous) => value.ReceivedAtUtc > previous ? value.ReceivedAtUtc : previous);
        }
    }

    private static async Task CommitMemberAsync(string path, IEnumerable<string> lines, CancellationToken token)
    {
        // Only the archive's single flush worker writes. A same-directory
        // replacement publishes a complete snapshot, even across interruption.
        var staging = path + ".pending";
        try
        {
            token.ThrowIfCancellationRequested();
            await using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: true))
            {
                if (File.Exists(path))
                {
                    await using var previous = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 65536, useAsync: true);
                    await previous.CopyToAsync(output, token);
                }
                await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                {
                    var bytes = Encoding.UTF8.GetBytes("\n" + string.Join('\n', lines) + "\n");
                    await gzip.WriteAsync(bytes, token);
                }
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(staging); }
            catch (IOException) { /* Leave the uncommitted file for the next write. */ }
            catch (UnauthorizedAccessException) { /* Preserve the original error. */ }
        }
    }

    public Task<HistoricalQuoteArchiveDay> ReadAsync(DateOnly tradingDate, CancellationToken cancellationToken) =>
        ReadCoreAsync(tradingDate, null, cancellationToken);

    public async Task<HistoricalReplayQuoteArchiveDay> ReadReplayAsync(DateOnly date, string market, string exchange,
        IReadOnlyDictionary<string, string> symbols, DateTimeOffset start, DateTimeOffset end, CancellationToken token,
        bool streamingOnly = false, bool tolerateUnreadableShards = false)
    {
        if (market is not ("NIFTY" or "SENSEX" or "BANKNIFTY") || exchange is not ("NSE" or "BSE"))
            throw new ArgumentException("Unsupported research market.");
        using var pinned = cloud?.Pin(date);
        if (streamingOnly && cloud?.Enabled == true)
        {
            await cloud.HydrateStreamingQuotesAsync(date, token);
            var remoteRoot = Path.Combine(cloud.CacheRoot, "quotes");
            if (HasDateFiles(remoteRoot, date, includeWebsite: false))
            {
                using var remote = new HistoricalQuoteArchive(clock, logger, remoteRoot);
                return await remote.ReadReplayAsync(date, market, exchange, symbols, start, end, token,
                    streamingOnly: true, tolerateUnreadableShards: tolerateUnreadableShards);
            }
            // Cloud configuration alone must not hide pre-cutover local history.
        }
        if (!Directory.Exists(root)) return new([], true);
        var gaps = false;
        var unreadable = new HashSet<HistoricalReplayGap>();
        var pathRoot = Path.Combine(root, "replay-paths");
        var partitioned = streamingOnly && Directory.EnumerateFiles(root, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "*.ndjson*")
            .Where(path => !ReplayBlobStorage.IsWebsiteQuotePath(path))
            .All(path => Path.GetFileName(path) is { } name && (name.EndsWith("-gaps.ndjson.gz", StringComparison.Ordinal) ||
                name.Length == 26 && name.EndsWith(".ndjson.gz", StringComparison.Ordinal) &&
                TimeOnly.TryParseExact(name.AsSpan(11, 5), "HH-mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)));
        var result = partitioned
            ? await FileHistoricalReplayQuotePath.BuildOrderedAsync(pathRoot, OrderMinuteShardsAsync(token), token)
            : await FileHistoricalReplayQuotePath.BuildAsync(pathRoot, ProjectAsync(token), token);
        return new(result, gaps) { UnreadableIntervals = unreadable.OrderBy(value => value.StartUtc).ToArray() };

        async IAsyncEnumerable<HistoricalReplayQuote> OrderMinuteShardsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var buffer = new List<HistoricalReplayQuote>();
            var minute = long.MinValue;
            await foreach (var quote in ProjectAsync(cancellationToken))
            {
                var next = quote.AvailableAtUtc.UtcTicks / TimeSpan.TicksPerMinute;
                if (next < minute) throw new InvalidDataException("Recorder quote lies outside its receipt-minute partition.");
                if (next != minute && buffer.Count != 0)
                {
                    buffer.Sort(HistoricalQuoteOrderComparer.Instance);
                    foreach (var row in buffer) yield return row;
                    buffer.Clear();
                }
                minute = next;
                if (buffer.Count == 100_000) throw new InvalidDataException("Recorder quote minute exceeds its bounded read size.");
                buffer.Add(quote);
            }
            buffer.Sort(HistoricalQuoteOrderComparer.Instance);
            foreach (var row in buffer) yield return row;
        }

        async IAsyncEnumerable<HistoricalReplayQuote> ProjectAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var receiptsSeen = new HashSet<Guid>();
            await foreach(var value in StreamDay(date,()=>gaps=true,cancellationToken, includeCloud: !streamingOnly,
                includeWebsite: !streamingOnly, unreadableShard: tolerateUnreadableShards ? MarkUnreadable : null)){
                if (value.Source == HistoricalQuoteObservation.GrowwRestFallback) continue;
                if (streamingOnly && value.Source != "GrowwFeedSample") continue;
                if (value.Request.Exchange != exchange || !value.Request.TradingSymbol.StartsWith(market, StringComparison.Ordinal) ||
                    value.ReceivedAtUtc < start || value.ReceivedAtUtc > end || !symbols.TryGetValue(value.Request.TradingSymbol, out var symbol)) continue;
                if (value.Source == HistoricalQuoteObservation.GrowwQuoteReceipt && !receiptsSeen.Add(value.Id)) continue;
                yield return HistoricalQuoteProjection.ToReplay(value, symbol);
            }
        }

        void MarkUnreadable(string path)
        {
            gaps = true;
            var name = Path.GetFileName(path);
            if (name.Length >= 16 && TimeOnly.TryParseExact(name.AsSpan(11, 5), "HH-mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var minute))
            {
                var from = new DateTimeOffset(start.UtcDateTime.Date.Add(minute.ToTimeSpan()), TimeSpan.Zero);
                unreadable.Add(new(from, from.AddMinutes(1), "Unreadable archive minute"));
            }
            else unreadable.Add(new(start, end, "Unreadable archive interval cannot be localized"));
        }
    }

    internal async Task<(Dictionary<Guid,List<Application.Diagnostics.PaperTimelineMark>> Marks,bool Gaps)> HeldMarksAsync(
        DateOnly date, IReadOnlyList<(Guid Id,string Exchange,string Symbol,DateTimeOffset From,DateTimeOffset To)> windows,CancellationToken token)
    {
        var result=new Dictionary<Guid,List<Application.Diagnostics.PaperTimelineMark>>();
        if(windows.Count==0)return(result,false);
        var byContract=windows.ToLookup(w=>(w.Exchange,w.Symbol));
        bool gaps=false;
        int count=0;
        var seen = new HashSet<(Guid Window, Guid Observation)>();
        await foreach(var v in StreamDay(date,()=>gaps=true,token)){
            if(v.Request.Segment!="FNO")continue;
            foreach(var w in byContract[(v.Request.Exchange,v.Request.TradingSymbol)]){
                if(v.ReceivedAtUtc<w.From || v.ReceivedAtUtc>=w.To)continue;
                if (!seen.Add((w.Id, v.Id))) continue;
                if(++count>maximumReadObservations)throw new InvalidDataException("Held-position quote limit exceeded.");
                var source=HistoricalQuoteProjection.ToReplay(v,w.Symbol).SourceAtUtc;
                if(!result.TryGetValue(w.Id,out var rows)) result[w.Id]=rows=[];
                rows.Add(new(v.ReceivedAtUtc,v.Quote.LastPrice,source) { Source = v.Source });
            }
        }
        return(result,gaps);
    }

    private async IAsyncEnumerable<HistoricalQuoteObservation> StreamDay(DateOnly date, Action gap,
        [EnumeratorCancellation] CancellationToken cancellationToken, bool includeCloud = true, bool includeWebsite = true,
        Action<string>? unreadableShard = null)
    {
            using var pinned = cloud?.Pin(date);
            var remoteAvailable = false;
            if (includeCloud && cloud?.Enabled == true)
            {
                await cloud.HydrateAsync("quotes", date, cancellationToken);
                var remoteRoot = Path.Combine(cloud.CacheRoot, "quotes");
                remoteAvailable = HasDateFiles(remoteRoot, date);
                if (remoteAvailable)
                {
                    using var remote = new HistoricalQuoteArchive(clock, logger, remoteRoot);
                    await foreach (var value in remote.StreamDay(date, gap, cancellationToken,
                        includeWebsite: includeWebsite, unreadableShard: unreadableShard)) yield return value;
                }
            }
            if(!Directory.Exists(root)){if (!remoteAvailable) gap();yield break;}
            var paths=Directory.GetFiles(root,date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"*.ndjson*")
                .Where(p=>(includeWebsite || !ReplayBlobStorage.IsWebsiteQuotePath(p)) &&
                    (p.EndsWith(".ndjson",StringComparison.Ordinal)||p.EndsWith(".ndjson.gz",StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal).ToArray();
            if((paths.Length==0 && !remoteAvailable) || dropped.GetValueOrDefault(date)>0 || buffered.GetValueOrDefault(date)>0 ||
               File.Exists(Path.Combine(root,date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+".capture-gap")) ||
               includeWebsite && File.Exists(Path.Combine(root,date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"-website.capture-gap")))gap();
            foreach (var path in paths)
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not null || info.Length > 256L * 1024 * 1024) throw new InvalidDataException("Invalid historical quote shard.");
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
                await using var gzip = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionMode.Decompress) : null;
                using var bounded = new HistoricalBoundedReadStream((Stream?)gzip ?? file, 2L * 1024 * 1024 * 1024);
                using var reader = new StreamReader(bounded, Encoding.UTF8);
                while (true)
                {
                    string? line;
                    try { line = await reader.ReadLineAsync(cancellationToken); }
                    catch (InvalidDataException) when (unreadableShard is not null)
                    {
                        // Later minute files are independent. The decoded prefix
                        // of a damaged minute does not certify that minute.
                        unreadableShard(path);
                        break;
                    }
                    if (line is null)
                    {
                        if (gzip is not null && file.Position < info.Length) unreadableShard?.Invoke(path);
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ArchiveLine? record;
                    try { record = JsonSerializer.Deserialize<ArchiveLine>(line); }
                    catch (JsonException) { gap(); unreadableShard?.Invoke(path); continue; }
                    var value = record?.Observation;
                    if (record is null || record.DroppedCount > 0 || value?.Request is null || value.Quote is null ||
                        value.Id == Guid.Empty || LocalDate(value.ReceivedAtUtc) != date || value.Quote.LastPrice <= 0m)
                    {
                        gap();
                        if (record is null || record.DroppedCount == 0) unreadableShard?.Invoke(path);
                        continue;
                    }
                    yield return value;
                }
            }
    }

    private static bool HasDateFiles(string directory, DateOnly date, bool includeWebsite = true) => Directory.Exists(directory) &&
        Directory.EnumerateFiles(directory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "*.ndjson*")
            .Any(path => (includeWebsite || !ReplayBlobStorage.IsWebsiteQuotePath(path)) &&
                (path.EndsWith(".ndjson", StringComparison.Ordinal) || path.EndsWith(".ndjson.gz", StringComparison.Ordinal)));

    public Task<HistoricalQuoteArchiveDay> ReadMarketAsync(DateOnly tradingDate, string market, CancellationToken cancellationToken)
    {
        if (market is not ("NIFTY" or "SENSEX" or "BANKNIFTY")) throw new ArgumentException("Unsupported research market.", nameof(market));
        return ReadCoreAsync(tradingDate, market, cancellationToken);
    }

    private async Task<HistoricalQuoteArchiveDay> ReadCoreAsync(DateOnly tradingDate, string? market, CancellationToken cancellationToken)
    {
        var observations = new Dictionary<Guid, HistoricalQuoteObservation>();
        var gaps = false;
        await foreach (var value in StreamDay(tradingDate, () => gaps = true, cancellationToken))
        {
                if (market is not null && !value.Request.TradingSymbol.StartsWith(market, StringComparison.Ordinal)) continue;
                if (!observations.ContainsKey(value.Id) && observations.Count >= maximumReadObservations)
                    throw new InvalidDataException("This research market/day exceeds the in-memory replay budget; shorten or partition the input, not silently truncate it.");
                if (!observations.TryAdd(value.Id, value) && observations[value.Id] != value) gaps = true;
        }
        return new(observations.Values.OrderBy(value => value.ReceivedAtUtc).ThenBy(value => value.Id).ToArray(), gaps);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
        while (pending.Count > 0 || queue.Reader.TryPeek(out _)) await FlushBatchAsync(cancellationToken);
        if (dropped.Any(value => value.Value > 0)) await FlushBatchAsync(cancellationToken);
    }

    private static DateOnly LocalDate(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
    private static bool SupportedSymbol(string symbol) => symbol.StartsWith("NIFTY", StringComparison.Ordinal) ||
        symbol.StartsWith("BANKNIFTY", StringComparison.Ordinal) || symbol.StartsWith("SENSEX", StringComparison.Ordinal);
    private sealed record ArchiveLine(HistoricalQuoteObservation? Observation, long DroppedCount);
}
