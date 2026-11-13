using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.Infrastructure.MarketData;

public sealed class HistoricalFeedCaptureOptions
{
    public const string SectionName = "MarketData:HistoricalReplayFeed";
    public bool Enabled { get; init; } = true;
    public bool AfterMarketBackfillEnabled { get; init; } = true;
    // Each derivative requires separate price and depth subscriptions.
    public int MaximumInstruments { get; init; } = 500;
    public int SampleSeconds { get; init; } = 2;
}

// A research-only sibling of the trading REST feeds. No orders or live decision
// engines are resolved. Read-only frozen settings define the capture universe.
internal sealed class GrowwHistoricalFeedCaptureService(IServiceScopeFactory scopes,
    IGrowwHistoricalFeedTransport transport, IHistoricalQuoteArchive archive,
    IOptions<HistoricalFeedCaptureOptions> options, TimeProvider clock, ResearchCapturePulse pulse,
    ILogger<GrowwHistoricalFeedCaptureService> logger) : BackgroundService
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);
    private static readonly Action<ILogger, string, Exception?> CaptureFailed = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(6753, "HistoricalFeedCaptureFailed"),
        "Historical streaming capture failed ({FailureType}); a retry is scheduled and research coverage is incomplete.");
    private readonly string root = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory,
        "data", "historical-replay", "feed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        var consecutiveFailures = 0;
        DateOnly? monitoredDate = null;
        ReplayDepthMonitor? depthMonitor = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var local = clock.GetUtcNow().ToOffset(India);
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                TimeOnly.FromDateTime(local.DateTime) < new TimeOnly(9, 0) ||
                TimeOnly.FromDateTime(local.DateTime) >= new TimeOnly(15, 35))
            { await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken); continue; }
            var date = DateOnly.FromDateTime(local.DateTime);
            if (date != monitoredDate)
            {
                var open = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), India);
                depthMonitor = new ReplayDepthMonitor(open, open.AddHours(6.25));
                monitoredDate = date;
            }
            pulse.Starting(date);
            try { await CaptureSessionAsync(date, stoppingToken, depthMonitor); pulse.Finished(); consecutiveFailures = 0; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                var failure = exception is HttpRequestException { StatusCode: { } status }
                    ? $"HTTP {(int)status} ({status})" : exception.GetType().Name;
                pulse.Failed(failure, IsSessionStarted(date));
                MarkSessionGap(date);
                // Never log transport/auth exception messages or NATS options:
                // credentials may be embedded in third-party error details.
                CaptureFailed(logger, pulse.Read(date, true).LastFailure ?? exception.GetType().Name, null);
                var snapshot = pulse.Read(date, true);
                consecutiveFailures = snapshot.SubscribedAtUtc is { } connected &&
                    clock.GetUtcNow() - connected >= TimeSpan.FromMinutes(1) ? 1 : Math.Min(6, consecutiveFailures + 1);
                await Task.Delay(RecoveryDelay(consecutiveFailures), clock, stoppingToken);
            }
        }
    }

    internal static TimeSpan RecoveryDelay(int consecutiveFailures) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Clamp(consecutiveFailures, 1, 6) - 1)));

    internal async Task CaptureSessionAsync(DateOnly date, CancellationToken stoppingToken,
        ReplayDepthMonitor? depthMonitor = null)
    {
        pulse.Stage("Instrument plan / pre-session anchors");
        var receipt = await CapturePolicyReceiptOnceAsync(date, stoppingToken);
        var (plan, coverage) = await LoadPlanAsync(date, receipt?.Settings.NiftySelection, stoppingToken);
        await RecordSessionAsync(date, plan, (day, token) => receipt is null
                ? CapturePolicyReceiptAsync(day, token) : Task.FromResult<SessionPolicyReceipt?>(receipt),
            CapturePriorThesisAsync, root, stoppingToken, coverage, depthMonitor);
    }

    // Separate input retrieval from the recording lifecycle so disconnect,
    // shutdown and checkpoint behavior can be exercised without a broker account.
    internal async Task RecordSessionAsync(DateOnly date, IReadOnlyList<HistoricalFeedInstrument> plan,
        Func<DateOnly, CancellationToken, Task<SessionPolicyReceipt?>> captureReceipt,
        Func<DateOnly, CancellationToken, Task<(bool Captured, PersistentDirectionalThesisV1? Thesis)>> capturePriorThesis,
        string outputDirectory, CancellationToken stoppingToken,
        ReplayContractCoverageInput? coverageInput = null, ReplayDepthMonitor? depthMonitor = null)
    {
        var now = clock.GetUtcNow();
        var open = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), India);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan);
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        var manifest = new HistoricalFeedCaptureManifest(date, now, null, null, fingerprint,
            options.Value.SampleSeconds, now > open, plan)
        { UniverseScope = "PreSessionStrikeBand", ContractSelectionVersion = RegularOptionContractSelector.Version };
        if (manifest.HasGaps) archive.MarkCaptureGap(date);
        pulse.Stage("Manifest storage");
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".json");
        await WriteManifestAsync(path, manifest, stoppingToken);
        var accumulator = new GrowwHistoricalFeedAccumulator(archive);
        var end = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 35)), India).ToUniversalTime();
        if (depthMonitor is null && plan.Any(value => value.Type is InstrumentType.CallOption or InstrumentType.PutOption))
            depthMonitor = new ReplayDepthMonitor(open, open.AddHours(6.25));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var timer = Task.Delay(end - now, clock, lifetime.Token);
        var captureGap = 0;
        var observedRanges = new ConcurrentDictionary<string, (decimal Low, decimal High)>(StringComparer.Ordinal);
        DateTimeOffset? subscribedAt = null;
        pulse.Stage("Broker authorization / subscription");
        var consume = transport.ConsumeAsync(plan, (instrument, kind, point, received) =>
            {
                accumulator.Observe(instrument, kind, point, received);
                if ((kind == "depth" || point.Last is > 0m) && point.SourceAtUtc <= received &&
                    received - point.SourceAtUtc <= TimeSpan.FromSeconds(30))
                {
                    pulse.Received(instrument.Market, received);
                    if (instrument.Type is InstrumentType.CallOption or InstrumentType.PutOption &&
                        (kind != "depth" || point.Bid is > 0m && point.Offer >= point.Bid &&
                         point.BidQuantity > 0 && point.OfferQuantity > 0))
                        pulse.OptionReceived(instrument.Market, kind, point.SourceAtUtc);
                }
                if (instrument.Type == InstrumentType.Index && kind != "depth" && point.Last is > 0m &&
                    point.SourceAtUtc <= received && received - point.SourceAtUtc <= TimeSpan.FromSeconds(30) &&
                    received >= open)
                    observedRanges.AddOrUpdate(instrument.Market, (point.Last.Value, point.Last.Value),
                        (_, range) => (Math.Min(range.Low, point.Last.Value), Math.Max(range.High, point.Last.Value)));
            },
            () =>
            {
                if (lifetime.IsCancellationRequested) return;
                var gapObserved = RecordGap(clock.GetUtcNow());
                pulse.Failed("Transport disconnected or dropped messages", gapObserved);
            },
            at => { subscribedAt = at; pulse.Subscribed(at, plan.Count, plan
                .Where(value => value.Type is InstrumentType.CallOption or InstrumentType.PutOption)
                .Select(value => value.Market).Distinct(StringComparer.Ordinal).ToArray()); pulse.Stage("Recording"); },
            (instrument, kind, at) =>
            {
                accumulator.Invalidate(instrument, kind, at);
                // A rejected frame affects one cached side, not the socket's
                // connection state. Retain regular-session gaps and let the
                // freshness watchdog detect a stalled market independently.
                var gapObserved = RecordGap(at);
                pulse.RejectedFrame(new(at, instrument.Market, instrument.TradingSymbol, kind), gapObserved);
            }, lifetime.Token);
        var sampling = SampleAsync(date, accumulator, at =>
        {
            if (depthMonitor is null) return;
            if (depthMonitor.Check(at, archive.ReadHealth(date))) RecordGap(at);
            pulse.OptionDepthAudit(depthMonitor.Snapshot());
        }, lifetime.Token);
        // Never hold subscription startup waiting for optional fidelity metadata.
        var receipt = captureReceipt(date, lifetime.Token);
        var priorThesis = capturePriorThesis(date, lifetime.Token);
        if (receipt.IsCompletedSuccessfully) UpdateCoverage(receipt.Result);
        // Persist metadata and progress DURING the session, not only at shutdown.
        // A crash must not erase an already captured pre-open policy receipt.
        var checkpoint = CheckpointAsync();
        try
        {
            var completed = await Task.WhenAny(consume, timer, sampling, checkpoint);
            if (completed == checkpoint) await checkpoint;
            if (completed == sampling) await sampling;
            if (completed == consume)
            {
                await consume;
                throw new InvalidDataException("Historical feed stopped before the session window ended.");
            }
            await timer;
            await lifetime.CancelAsync();
            try { await consume; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            try { await sampling; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            if (depthMonitor is not null)
            {
                if (depthMonitor.Check(clock.GetUtcNow(), archive.ReadHealth(date))) RecordGap(clock.GetUtcNow());
                pulse.OptionDepthAudit(depthMonitor.Snapshot());
            }
            manifest = manifest with { SubscribedAtUtc = subscribedAt, CompletedAtUtc = clock.GetUtcNow(),
                HasGaps = manifest.HasGaps || Volatile.Read(ref captureGap) != 0 || subscribedAt is null ||
                    subscribedAt > open };
        }
        catch (Exception exception)
        {
            manifest = manifest with { SubscribedAtUtc = subscribedAt, HasGaps = true,
                InterruptionReason = stoppingToken.IsCancellationRequested ? "Recorder shutdown before capture completed" :
                    pulse.Read(date, true).Stage + ": " + exception.GetType().Name };
            MarkSessionGap(date);
            throw;
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await consume; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception) when (consume.IsFaulted) { /* Already observed by the main await; retain the failure manifest. */ }
            try { await sampling; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception) when (sampling.IsFaulted) { manifest = manifest with { HasGaps = true }; }
            try { await checkpoint; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception) when (checkpoint.IsFaulted) { manifest = manifest with { HasGaps = true }; }
            var prior = await priorThesis;
            manifest = manifest with { PolicyReceipt = await receipt,
                LastRejectedFrame = pulse.Read(date, true).LastRejectedFrame,
                SensexPriorThesisStateCaptured = prior.Captured, PriorSensexThesis = prior.Thesis,
                OptionDepthAudit = pulse.Read(date, true).OptionDepthAudit };
            UpdateCoverage(manifest.PolicyReceipt);
            await WriteManifestAsync(path, manifest, CancellationToken.None);
        }

        async Task CheckpointAsync()
        {
            using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
            while (await ticker.WaitForNextTickAsync(lifetime.Token))
            {
                var prior = priorThesis.IsCompletedSuccessfully ? priorThesis.Result : default;
                var policy = receipt.IsCompletedSuccessfully ? receipt.Result : null;
                pulse.Metadata(policy?.ValidFor(date) == true, prior.Captured);
                UpdateCoverage(policy);
                if (coverageInput is not null && policy is not null)
                {
                    var problems = observedRanges.SelectMany(pair => ReplayContractCoverage.CheckRange(
                        coverageInput.Contracts, plan.Where(c => c.Market == pair.Key).ToArray(), pair.Key,
                        date, pair.Value.Low, pair.Value.High, policy.Settings.NiftySelection)).ToArray();
                    manifest = manifest with { ContractCoverageIssues = problems };
                    pulse.ContractCoverageIssues(problems);
                }
                manifest = manifest with { SubscribedAtUtc = subscribedAt,
                    LastCheckpointAtUtc = clock.GetUtcNow(), HasGaps = manifest.HasGaps || Volatile.Read(ref captureGap) != 0 ||
                        subscribedAt is null || subscribedAt > open,
                    LastRejectedFrame = pulse.Read(date, true).LastRejectedFrame,
                    OptionDepthAudit = pulse.Read(date, true).OptionDepthAudit,
                    PolicyReceipt = policy,
                    SensexPriorThesisStateCaptured = prior.Captured, PriorSensexThesis = prior.Thesis };
                await WriteManifestAsync(path, manifest, lifetime.Token);
            }
        }

        bool RecordGap(DateTimeOffset at)
        {
            if (at < open) return false;
            if (Interlocked.Exchange(ref captureGap, 1) == 0) archive.MarkCaptureGap(date);
            return true;
        }

        void UpdateCoverage(SessionPolicyReceipt? policy)
        {
            // Keep the actual pre-open receipt; a late recovery must never rewrite
            // history as a successful pre-session check.
            if (coverageInput is null || manifest.ContractPreflight?.Ready == true) return;
            var preflight = ReplayContractCoverage.Preflight(date, clock.GetUtcNow(), plan, coverageInput, policy);
            manifest = manifest with { ContractPreflight = preflight };
            pulse.ContractCoverage(preflight);
        }
    }

    private void MarkSessionGap(DateOnly date)
    {
        if (IsSessionStarted(date))
            archive.MarkCaptureGap(date);
    }
    private bool IsSessionStarted(DateOnly date) =>
        clock.GetUtcNow() >= new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), India);

    private async Task<(bool Captured, PersistentDirectionalThesisV1? Thesis)> CapturePriorThesisAsync(DateOnly date, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { return (true, await LoadPriorThesisAsync(date, token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return default; }
            catch (Exception ex) { PolicyReceiptFailed(logger, ex.GetType().Name, null); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), clock, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return default; }
        }
        return default;
    }

    private async Task<SessionPolicyReceipt?> CapturePolicyReceiptAsync(DateOnly date,CancellationToken token)
    {
        do
        {
            var captured = await CapturePolicyReceiptOnceAsync(date, token);
            if (captured is not null || IsSessionStarted(date) || token.IsCancellationRequested) return captured;
            try { await Task.Delay(TimeSpan.FromSeconds(5), clock, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        } while (!token.IsCancellationRequested);
        return null;
    }

    private async Task<SessionPolicyReceipt?> CapturePolicyReceiptOnceAsync(DateOnly date,CancellationToken token)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IConfiguration>() is { } configuration && ReplayRecorderHosting.IsRecorder(configuration))
                return await scope.ServiceProvider.GetRequiredService<ReplayBlobStorage>().ReadWebsitePolicyAsync(date, token);
            var started = clock.GetUtcNow();
            var build = await scope.ServiceProvider.GetRequiredService<IHistoricalPolicyBuildStore>().CaptureCurrentAsync(token);
            var config = await scope.ServiceProvider.GetRequiredService<SessionPolicySettings>().ReadAsync(token);
            var at = clock.GetUtcNow();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            if (await db.AuditLogs.AsNoTracking().AnyAsync(x=>x.Action=="IndexTradingLotsChanged" &&
                x.OccurredAtUtc>=started && x.OccurredAtUtc<=at,token)) return null;
            return SessionPolicyReceipt.Create(date,at,build.Id,config);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception ex) { PolicyReceiptFailed(logger,ex.GetType().Name,null); return null; }
    }

    private static readonly Action<ILogger,string,Exception?> PolicyReceiptFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,new EventId(6755,"ReplayPolicyReceiptMissing"),"Paper fidelity settings capture unavailable ({FailureType}).");

    private async Task SampleAsync(DateOnly date, GrowwHistoricalFeedAccumulator accumulator,
        Action<DateTimeOffset> auditPersistedDepth, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SampleSeconds), clock);
        while (await timer.WaitForNextTickAsync(token))
        {
            var now = clock.GetUtcNow();
            auditPersistedDepth(now);
            if (now >= new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), India) &&
                now < new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 30)), India) &&
                pulse.Read(date, true).HasStalledMarket(now))
            {
                pulse.Stage("Feed silence over 30 seconds");
                throw new TimeoutException("Historical feed stalled; reconnect required.");
            }
            accumulator.Sample(now);
        }
    }

    private async Task<(IReadOnlyList<HistoricalFeedInstrument> Plan, ReplayContractCoverageInput Coverage)> LoadPlanAsync(
        DateOnly date, NiftyOptionSelectionOptions? selection, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var rows = await db.Instruments.AsNoTracking().Where(value => value.IsActive &&
            (value.Exchange == "NSE" || value.Exchange == "BSE") &&
            (value.TradingSymbol.StartsWith("NIFTY") || value.TradingSymbol.StartsWith("BANKNIFTY") || value.TradingSymbol.StartsWith("SENSEX")) &&
            (value.Type == InstrumentType.Index || value.Type == InstrumentType.Future ||
             value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption)).ToArrayAsync(token);
        var anchors = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var market in new[] { "NIFTY", "SENSEX", "BANKNIFTY" })
        {
            var index = rows.Single(value => value.TradingSymbol == market && value.Type == InstrumentType.Index);
            pulse.Stage("Pre-session anchor / " + market);
            var (oldest, before) = AnchorWindowUtc(date);
            var anchor = await db.Candles.AsNoTracking().Where(value => value.InstrumentId == index.Id &&
                    value.IntervalSeconds == 60 && value.Source == "Groww" && value.OpenTimeUtc >= oldest && value.OpenTimeUtc < before)
                .OrderByDescending(value => value.OpenTimeUtc).Select(value => (decimal?)value.Close).FirstOrDefaultAsync(token);
            if (anchor is not > 0m) throw new InvalidOperationException("Replay pre-session index anchor is unavailable: " + market);
            anchors.Add(market, anchor.Value);
        }
        var source = rows.Select(value => new HistoricalFeedInstrument(
            value.TradingSymbol.StartsWith("BANKNIFTY", StringComparison.Ordinal) ? "BANKNIFTY" :
                value.TradingSymbol.StartsWith("SENSEX", StringComparison.Ordinal) ? "SENSEX" : "NIFTY",
            value.Exchange, value.Segment == InstrumentSegment.Cash ? "CASH" : "FNO", value.ExchangeToken ?? "",
            value.TradingSymbol, value.Type, value.ExpiryDate, value.StrikePrice, value.LotSize, value.TickSize)).ToArray();
        selection ??= (await scope.ServiceProvider.GetRequiredService<SessionPolicySettings>().ReadAsync(token)).NiftySelection;
        var plan = HistoricalFeedSubscriptionPlanner.Select(date, source, options.Value.MaximumInstruments, anchors, selection);
        return (plan, new(ReplayContractCoverage.Contracts(source), anchors));
    }

    // Npgsql timestamptz parameters require offset zero, even for comparisons.
    // Define the session in IST, then convert the same instants before querying.
    internal static (DateTimeOffset Oldest, DateTimeOffset Before) AnchorWindowUtc(DateOnly date) =>
        (new DateTimeOffset(date.AddDays(-7).ToDateTime(TimeOnly.MinValue), India).ToUniversalTime(),
         new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), India).ToUniversalTime());

    private async Task<PersistentDirectionalThesisV1?> LoadPriorThesisAsync(DateOnly date, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), India).ToUniversalTime();
        var underlyingId = await db.Instruments.AsNoTracking().Where(value => value.Exchange == "BSE" &&
                value.Type == InstrumentType.Index && value.TradingSymbol == "SENSEX")
            .Select(value => value.Id).SingleAsync(token);
        var json = await db.MarketStrategyAudits.AsNoTracking().Where(value => value.Market == "sensex" &&
                value.UnderlyingInstrumentId == underlyingId &&
                value.CandleTimeUtc < start && value.Outcome == PersistentDirectionalThesisAnalyzerV1.AuditOutcome)
            .OrderByDescending(value => value.CandleTimeUtc).Select(value => value.ReasonsJson).FirstOrDefaultAsync(token);
        return DirectionalThesisShadowAudit.Read(json);
    }

    private static async Task WriteManifestAsync(string path, HistoricalFeedCaptureManifest manifest, CancellationToken token)
    {
        var pending = path + ".pending";
        await File.WriteAllBytesAsync(pending, JsonSerializer.SerializeToUtf8Bytes(manifest), token);
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(pending, path, overwrite: true); return; }
            catch (Exception exception) when (OperatingSystem.IsWindows() && attempt < 4 &&
                exception is IOException or UnauthorizedAccessException &&
                (exception.HResult & 0xffff) is 5 or 32 or 33)
            {
                // A concurrent manifest reader can briefly block an atomic
                // replace on Windows. Persistent failures still fail capture.
                await Task.Delay(TimeSpan.FromMilliseconds(15), token);
            }
        }
    }
}
