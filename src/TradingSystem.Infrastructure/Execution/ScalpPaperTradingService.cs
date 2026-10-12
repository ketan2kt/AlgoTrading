using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

/// <summary>
/// An isolated, paper-only high-frequency research engine. It shares read-only market data with
/// the champion engines, but uses distinct market keys, positions, audits, P&amp;L and exits. Its
/// strategy prefix is intentionally excluded from AutomaticLiveExecutionService discovery.
/// </summary>
internal sealed partial class ScalpPaperTradingService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    ILiveMarketDataPublisher publisher,
    IOptions<ScalpPaperTradingOptions> options,
    IOptions<MarketDataOptions> marketOptions,
    TimeProvider timeProvider,
    ILogger<ScalpPaperTradingService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    private readonly Dictionary<string, DateTimeOffset> lastEvaluated = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, decimal> peakByPosition = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var market in new[] { TradingMarketCatalog.NiftyScalp, TradingMarketCatalog.SensexScalp })
            {
                try { await RunMarketAsync(market, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    LogCycleFailed(logger, market.Code, exception);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.EvaluationIntervalSeconds), stoppingToken);
        }
    }

    private async Task RunMarketAsync(TradingMarketDefinition market, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var now = timeProvider.GetUtcNow();
        var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        if (india.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        var localTime = TimeOnly.FromDateTime(india.DateTime);
        if (localTime < market.SessionStart || localTime > market.SessionEnd) return;

        var underlying = await db.Instruments.FirstOrDefaultAsync(value =>
            value.Exchange == market.Exchange && value.Segment == market.InstrumentSegment &&
            value.Type == InstrumentType.Index && value.TradingSymbol == market.UnderlyingSymbol && value.IsActive,
            cancellationToken);
        if (underlying is null) return;

        var sessionStartUtc = new DateTimeOffset(india.Date, india.Offset).ToUniversalTime();
        var candles = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == underlying.Id &&
                value.IntervalSeconds == marketOptions.Value.CandleIntervalSeconds &&
                value.Source == "Groww" && value.OpenTimeUtc >= sessionStartUtc)
            .OrderByDescending(value => value.OpenTimeUtc).Take(90)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        if (candles.Count < 14) return;
        var oneMinuteBars = candles.Select(value => new ScalpPriceBar(value.OpenTimeUtc, value.Open,
            value.High, value.Low, value.Close, value.Volume)).ToArray();
        var bars = ScalpTradingPolicy.AggregateCompletedBars(oneMinuteBars, options.Value.SignalTimeframeMinutes);
        if (bars.Count < 14) return;
        var latest = bars[^1];
        var signal = ScalpTradingPolicy.Evaluate(bars);

        await ManagePositionsAsync(db, market, signal, now, cancellationToken);
        if (lastEvaluated.GetValueOrDefault(market.Code) == latest.TimeUtc)
        {
            await PublishAsync(scope.ServiceProvider, market.Code, cancellationToken);
            return;
        }
        lastEvaluated[market.Code] = latest.TimeUtc;

        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id,
            latest.TimeUtc, signal.Direction is null ? "ScalpCandidateRejected" : "ScalpCandidate",
            signal.Confidence, JsonSerializer.Serialize(new
            {
                version = "scalp-v2", signalTimeframeMinutes = options.Value.SignalTimeframeMinutes,
                signal.Direction, signal.Regime, signal.Atr,
                signal.Evidence, paperOnly = true, liveEligible = false
            })));
        await db.SaveChangesAsync(cancellationToken);

        if (signal.Direction is null || signal.Confidence < options.Value.MinimumConfidence ||
            localTime < TimeOnly.ParseExact(options.Value.EntryWindowStart, "HH:mm") ||
            localTime >= TimeOnly.ParseExact(options.Value.EntryCutoff, "HH:mm"))
        {
            await PublishAsync(scope.ServiceProvider, market.Code, cancellationToken);
            return;
        }
        if (await db.MarketPaperPositions.AnyAsync(value => value.Market == market.Code &&
                value.Status == "Active", cancellationToken))
        {
            await PublishAsync(scope.ServiceProvider, market.Code, cancellationToken);
            return;
        }
        var realised = await db.MarketPaperPositions.AsNoTracking().Where(value =>
                value.Market == market.Code && value.OpenedAtUtc >= sessionStartUtc && value.Status != "Active")
            .SumAsync(value => value.RealisedPnl, cancellationToken);
        if (realised <= -options.Value.MaximumDailyLoss)
        {
            await RecordAsync(db, market, underlying.Id, latest.TimeUtc, "ScalpDailyLossBlocked",
                signal.Confidence, ["The isolated scalp-engine daily loss boundary has been reached."], cancellationToken);
            await PublishAsync(scope.ServiceProvider, market.Code, cancellationToken);
            return;
        }
        var cohortCount = await db.MarketStrategyAudits.AsNoTracking().CountAsync(value =>
            value.Market == market.Code && value.CandleTimeUtc >= sessionStartUtc &&
            value.Outcome == "ScalpPositionOpened", cancellationToken);
        if (cohortCount >= options.Value.MaximumCohortsPerDay)
        {
            await PublishAsync(scope.ServiceProvider, market.Code, cancellationToken);
            return;
        }
        var latestClose = await db.MarketPaperPositions.AsNoTracking().Where(value =>
                value.Market == market.Code && value.ClosedAtUtc != null)
            .MaxAsync(value => (DateTimeOffset?)value.ClosedAtUtc, cancellationToken);
        if (latestClose is { } closedAt && now - closedAt < TimeSpan.FromMinutes(options.Value.CooldownMinutes))
        {
            await PublishAsync(scope.ServiceProvider, market.Code, cancellationToken);
            return;
        }

        var execution = await SelectOptionAsync(db, market, signal.Direction.Value, latest.Close,
            DateOnly.FromDateTime(india.Date), cancellationToken);
        if (execution is null)
        {
            await RecordAsync(db, market, underlying.Id, latest.TimeUtc, "ScalpInstrumentUnavailable",
                signal.Confidence, ["No eligible option contract is synchronised."], cancellationToken);
            await PublishAsync(scope.ServiceProvider, market.Code, cancellationToken);
            return;
        }
        GrowwQuote quote;
        try
        {
            quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
                execution.TradingSymbol), cancellationToken);
        }
        catch (GrowwApiException exception)
        {
            await RecordAsync(db, market, underlying.Id, latest.TimeUtc, "ScalpQuoteUnavailable",
                signal.Confidence, [exception.Message], cancellationToken);
            await PublishAsync(scope.ServiceProvider, market.Code, cancellationToken);
            return;
        }
        var pricing = OptionPaperTradePricing.ForPermissiveSimulation(quote);
        if (!pricing.Approved) return;
        var entry = pricing.EntryPrice;
        var tick = execution.TickSize > 0
            ? InstrumentPriceIncrement.FromGrowwInstrumentMaster(execution.TickSize) : .05m;
        var risk = Math.Max(3m, entry * options.Value.StopLossPercent / 100m);
        var stop = RoundDown(Math.Max(tick, entry - risk), tick);
        var oneLotCharges = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, entry, execution.LotSize),
            new(PaperOptionTransactionSide.Sell, entry + options.Value.QuickProfitPoints, execution.LotSize)]).Total;
        var chargeCover = oneLotCharges / execution.LotSize;
        var quickPoints = Math.Max(options.Value.QuickProfitPoints, chargeCover * 1.5m);
        var quickTarget = RoundUp(entry + quickPoints, tick);
        var runnerTarget = RoundUp(entry + risk * options.Value.RunnerTargetRiskMultiple, tick);

        var cohortId = Guid.NewGuid();
        var tranches = Math.Max(2, options.Value.LotsPerCohort);
        for (var index = 0; index < tranches; index++)
        {
            var role = index == 0 ? "Quick" : "Runner";
            var target = index == 0 ? quickTarget : runnerTarget;
            db.MarketPaperPositions.Add(new(Guid.NewGuid(), market.Code, underlying.Id, execution.Id,
                $"ScalpLab|{role}|{cohortId:N}", Direction.Buy, execution.LotSize,
                entry, stop, target, now));
        }
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id,
            latest.TimeUtc, "ScalpPositionOpened", signal.Confidence, JsonSerializer.Serialize(new
            {
                version = "scalp-v2", signalTimeframeMinutes = options.Value.SignalTimeframeMinutes,
                cohortId, paperOnly = true, liveEligible = false,
                underlyingDirection = signal.Direction.ToString(), execution = execution.TradingSymbol,
                entry, stop, quickTarget, runnerTarget, lots = tranches, lotSize = execution.LotSize,
                estimatedRoundTripChargesPerLot = oneLotCharges, signal.Evidence
            })));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(scope.ServiceProvider, market.Code, cancellationToken);
    }

    private async Task ManagePositionsAsync(TradingDbContext db, TradingMarketDefinition market,
        ScalpSignal signal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = await db.MarketPaperPositions.Where(value => value.Market == market.Code &&
            value.Status == "Active").ToListAsync(cancellationToken);
        foreach (var position in active)
        {
            var instrument = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(value =>
                value.Id == position.ExecutionInstrumentId, cancellationToken);
            if (instrument is null) continue;
            GrowwQuote quote;
            try
            {
                quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
                    instrument.TradingSymbol), cancellationToken);
            }
            catch (GrowwApiException) { continue; }
            var price = quote.BidPrice is > 0 ? quote.BidPrice.Value : quote.LastPrice;
            if (price <= 0m) continue;
            var peak = Math.Max(peakByPosition.GetValueOrDefault(position.Id, position.CurrentPrice), price);
            peakByPosition[position.Id] = peak;
            var chargesAtMark = PaperTradingCostModel.CalculateOptionCharges([
                new(PaperOptionTransactionSide.Buy, position.EntryPrice, position.Quantity),
                new(PaperOptionTransactionSide.Sell, price, position.Quantity)]).Total;
            var breakEven = position.EntryPrice + chargesAtMark / position.Quantity + .25m;
            var trailing = position.Strategy.Contains("|Runner|", StringComparison.Ordinal)
                ? ScalpTradingPolicy.TrailingStop(position.EntryPrice, position.StopLoss, peak,
                    options.Value.RunnerActivationPoints, options.Value.RunnerTrailingGapPoints, breakEven)
                : peak - position.EntryPrice >= options.Value.QuickProfitPoints
                    ? Math.Max(position.StopLoss, breakEven) : position.StopLoss;
            position.Mark(price, trailing);

            var age = now - position.OpenedAtUtc;
            var expectedUnderlying = instrument.Type == InstrumentType.PutOption ? Direction.Sell : Direction.Buy;
            var reversed = signal.Direction is { } direction && direction != expectedUnderlying &&
                           price <= position.EntryPrice;
            var stopHit = price <= position.StopLoss;
            var targetHit = price >= position.Target;
            var runner = position.Strategy.Contains("|Runner|", StringComparison.Ordinal);
            var stale = age >= TimeSpan.FromMinutes(runner
                ? options.Value.RunnerMaximumHoldMinutes : options.Value.MaximumHoldMinutes) &&
                        price <= breakEven;
            var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
            var forced = TimeOnly.FromDateTime(india.DateTime) >=
                         TimeOnly.ParseExact(options.Value.ForcedExit, "HH:mm");
            if (!stopHit && !targetHit && !reversed && !stale && !forced) continue;

            var status = targetHit ? "TargetHit" : stopHit ? "StopLossHit" : reversed
                ? "FastFailureExit" : stale ? "StaleExit" : "TimeExit";
            position.Close(price, chargesAtMark, status, now);
            peakByPosition.Remove(position.Id);
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                position.UnderlyingInstrumentId, now, "ScalpPositionClosed", 0m,
                JsonSerializer.Serialize(new { positionId = position.Id, position.Strategy,
                    status, position.EntryPrice, exit = price, peak, position.StopLoss,
                    position.Target, position.RealisedPnl, charges = chargesAtMark, ageMinutes = age.TotalMinutes })));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Instrument?> SelectOptionAsync(TradingDbContext db,
        TradingMarketDefinition market, Direction direction, decimal underlyingPrice,
        DateOnly today, CancellationToken cancellationToken)
    {
        var candidates = await db.Instruments.AsNoTracking().Where(value =>
                value.Exchange == market.Exchange && value.Segment == InstrumentSegment.FuturesAndOptions &&
                value.IsActive && value.ExpiryDate >= today && value.ExpiryDate <= today.AddDays(options.Value.MaximumDaysToExpiry) &&
                value.TradingSymbol.StartsWith(market.ExecutionUnderlying) &&
                (direction == Direction.Buy ? value.Type == InstrumentType.CallOption : value.Type == InstrumentType.PutOption))
            .Select(value => new NiftyOptionContractCandidate(value.Id, value.TradingSymbol, value.Type,
                value.ExpiryDate!.Value, value.StrikePrice!.Value, value.LotSize, value.TickSize))
            .ToArrayAsync(cancellationToken);
        var strikeStep = market == TradingMarketCatalog.SensexScalp ? 100m : 50m;
        var selected = NiftyOptionContractSelector.Select(candidates, direction, underlyingPrice, today,
            new NiftyOptionSelectionOptions(options.Value.MaximumDaysToExpiry, 1, 0, strikeStep));
        return selected is null ? null : await db.Instruments.FirstAsync(value =>
            value.Id == selected.InstrumentId, cancellationToken);
    }

    private static async Task RecordAsync(TradingDbContext db, TradingMarketDefinition market,
        Guid underlyingId, DateTimeOffset time, string outcome, decimal confidence,
        IReadOnlyList<string> evidence, CancellationToken cancellationToken)
    {
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlyingId, time,
            outcome, confidence, JsonSerializer.Serialize(evidence)));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task PublishAsync(IServiceProvider provider, string market,
        CancellationToken cancellationToken)
    {
        var reader = provider.GetRequiredService<ITradingWorkspaceReader>();
        var snapshot = await reader.GetAsync(market, 1_500, cancellationToken);
        await publisher.PublishAsync(market, snapshot, cancellationToken);
    }

    private static decimal RoundDown(decimal value, decimal tick) => Math.Floor(value / tick) * tick;
    private static decimal RoundUp(decimal value, decimal tick) => Math.Ceiling(value / tick) * tick;

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scalp paper cycle failed for {Market}.")]
    private static partial void LogCycleFailed(ILogger logger, string market, Exception exception);
}
