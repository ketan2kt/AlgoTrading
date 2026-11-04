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
    long maximumArchiveBytes = 5L * 1024 * 1024 * 1024) : BackgroundService, IHistoricalQuoteArchive
{
    private readonly string root = directory ?? Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory,
        "data", "historical-replay", "quotes");
    private readonly Channel<HistoricalQuoteObservation> queue = Channel.CreateBounded<HistoricalQuoteObservation>(
        new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<DateOnly, long> dropped = new();
    private readonly ConcurrentDictionary<DateOnly, long> buffered = new();
    private readonly List<HistoricalQuoteObservation> pending = [];
    private DateTimeOffset storageCheckedAt = DateTimeOffset.MinValue;
    private bool storageAvailable;
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
        }
    }

    public void MarkCaptureGap(DateOnly tradingDate) => dropped.AddOrUpdate(tradingDate, 1, static (_, count) => count + 1);

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
            storageAvailable = bytes < maximumArchiveBytes && (!drive.IsReady || drive.AvailableFreeSpace >= 512L * 1024 * 1024);
            storageCheckedAt = clock.GetUtcNow();
        }
        var gaps = dropped.Where(value => value.Value > 0).ToArray();
        var dates = pending.Select(value => LocalDate(value.ReceivedAtUtc)).Concat(gaps.Select(value => value.Key)).Distinct();
        foreach (var date in dates)
        {
            if (!storageAvailable)
            {
                // Preserve existing history; never fill the application disk
                // or retry an unlimited research backlog into the live host.
                await File.WriteAllTextAsync(Path.Combine(root, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".capture-gap"),
                    "Research quote storage capacity reached.", token);
                continue;
            }
            var lines = pending.Where(value => LocalDate(value.ReceivedAtUtc) == date)
                .Select(value => JsonSerializer.Serialize(new ArchiveLine(value, 0))).ToList();
            var loss = gaps.FirstOrDefault(value => value.Key == date).Value;
            if (loss > 0) lines.Add(JsonSerializer.Serialize(new ArchiveLine(null, loss)));
            // A leading newline resynchronises after an interrupted partial write.
            var hour = pending.Where(value => LocalDate(value.ReceivedAtUtc) == date)
                .Select(value => value.ReceivedAtUtc.Hour).DefaultIfEmpty(0).Min();
            var path = Path.Combine(root, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                "-" + hour.ToString("D2", CultureInfo.InvariantCulture) + ".ndjson.gz");
            await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 65536, useAsync: true);
            await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
            var bytes = Encoding.UTF8.GetBytes("\n" + string.Join('\n', lines) + "\n");
            await gzip.WriteAsync(bytes, token);
        }
        foreach (var gap in gaps) dropped.AddOrUpdate(gap.Key, 0, (_, count) => Math.Max(0, count - gap.Value));
        foreach (var group in pending.GroupBy(value => LocalDate(value.ReceivedAtUtc)))
            buffered.AddOrUpdate(group.Key, 0, (_, count) => Math.Max(0, count - group.Count()));
        pending.Clear();
    }

    public Task<HistoricalQuoteArchiveDay> ReadAsync(DateOnly tradingDate, CancellationToken cancellationToken) =>
        ReadCoreAsync(tradingDate, null, cancellationToken);

    public async Task<HistoricalReplayQuoteArchiveDay> ReadReplayAsync(DateOnly date, string market, string exchange,
        IReadOnlyDictionary<string, string> symbols, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        if (market is not ("NIFTY" or "SENSEX" or "BANKNIFTY") || exchange is not ("NSE" or "BSE"))
            throw new ArgumentException("Unsupported research market.");
        if (!Directory.Exists(root)) return new([], true);
        var paths = Directory.GetFiles(root, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "*.ndjson*")
            .Where(value => value.EndsWith(".ndjson", StringComparison.Ordinal) || value.EndsWith(".ndjson.gz", StringComparison.Ordinal)).ToArray();
        var gaps = paths.Length == 0 || dropped.GetValueOrDefault(date) > 0 || buffered.GetValueOrDefault(date) > 0 ||
            File.Exists(Path.Combine(root, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".capture-gap"));
        var result = await FileHistoricalReplayQuotePath.BuildAsync(Path.Combine(root, "replay-paths"), ProjectAsync(token), token);
        return new(result, gaps);

        async IAsyncEnumerable<HistoricalReplayQuote> ProjectAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var path in paths)
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not null || info.Length > 256L * 1024 * 1024) throw new InvalidDataException("Invalid historical quote shard.");
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
                await using var gzip = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionMode.Decompress) : null;
                using var bounded = new HistoricalBoundedReadStream((Stream?)gzip ?? file, 2L * 1024 * 1024 * 1024);
                using var reader = new StreamReader(bounded, Encoding.UTF8);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ArchiveLine? record;
                    try { record = JsonSerializer.Deserialize<ArchiveLine>(line); }
                    catch (JsonException) { gaps = true; continue; }
                    var value = record?.Observation;
                    if (record is null || record.DroppedCount > 0 || value?.Request is null || value.Quote is null ||
                        value.Id == Guid.Empty || LocalDate(value.ReceivedAtUtc) != date || value.Quote.LastPrice <= 0m)
                    { gaps = true; continue; }
                    if (value.Request.Exchange != exchange || !value.Request.TradingSymbol.StartsWith(market, StringComparison.Ordinal) ||
                        value.ReceivedAtUtc < start || value.ReceivedAtUtc > end || !symbols.TryGetValue(value.Request.TradingSymbol, out var symbol)) continue;
                    yield return HistoricalQuoteProjection.ToReplay(value, symbol);
                }
            }
        }
    }

    public Task<HistoricalQuoteArchiveDay> ReadMarketAsync(DateOnly tradingDate, string market, CancellationToken cancellationToken)
    {
        if (market is not ("NIFTY" or "SENSEX" or "BANKNIFTY")) throw new ArgumentException("Unsupported research market.", nameof(market));
        return ReadCoreAsync(tradingDate, market, cancellationToken);
    }

    private async Task<HistoricalQuoteArchiveDay> ReadCoreAsync(DateOnly tradingDate, string? market, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return new([], true);
        var paths = Directory.GetFiles(root, tradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "*.ndjson*")
            .Where(path => path.EndsWith(".ndjson", StringComparison.Ordinal) || path.EndsWith(".ndjson.gz", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) return new([], true);
        var observations = new Dictionary<Guid, HistoricalQuoteObservation>();
        var gaps = dropped.GetValueOrDefault(tradingDate) > 0 || buffered.GetValueOrDefault(tradingDate) > 0 ||
            File.Exists(Path.Combine(root, tradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".capture-gap"));
        foreach (var path in paths)
        {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || info.Length > 256L * 1024 * 1024)
            throw new InvalidDataException("Historical quote archive is invalid or exceeds its shard size limit.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
        await using var gzip = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true) : null;
        using var bounded = new HistoricalBoundedReadStream((Stream?)gzip ?? file, 2L * 1024 * 1024 * 1024);
        using var reader = new StreamReader(bounded, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<ArchiveLine>(line);
                if (record is null || record.DroppedCount > 0) { gaps = true; continue; }
                var value = record.Observation;
                if (value is null || value.Id == Guid.Empty || LocalDate(value.ReceivedAtUtc) != tradingDate ||
                    value.Request is null || value.Quote is null || value.Quote.LastPrice <= 0m)
                { gaps = true; continue; }
                if (market is not null && !value.Request.TradingSymbol.StartsWith(market, StringComparison.Ordinal)) continue;
                if (!observations.ContainsKey(value.Id) && observations.Count >= maximumReadObservations)
                    throw new InvalidDataException("This research market/day exceeds the in-memory replay budget; shorten or partition the input, not silently truncate it.");
                if (!observations.TryAdd(value.Id, value) && observations[value.Id] != value) gaps = true;
            }
            catch (JsonException) { gaps = true; }
        }
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
