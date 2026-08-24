using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Application.MarketData;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed partial class MultiMarketPaperTradingService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    IOptions<AutomatedPaperTradingOptions> options,
    IOptions<MarketDataOptions> marketOptions,
    TimeProvider timeProvider,
    ILogger<MultiMarketPaperTradingService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    private readonly Dictionary<string, DateTimeOffset> lastEvaluated = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var market in new[] { TradingMarketCatalog.Sensex, TradingMarketCatalog.NaturalGas })
            {
                try { await RunMarketAsync(market, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) { LogCycleFailed(logger, market.Code, exception); }
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
        var today = DateOnly.FromDateTime(india.Date);
        var underlying = await FindUnderlyingAsync(db, market, today, cancellationToken);
        if (underlying is null) return;

        await ManagePositionsAsync(db, market, now, cancellationToken);

        var interval = marketOptions.Value.CandleIntervalSeconds;
        var candles = await db.Candles.AsNoTracking().Where(value => value.InstrumentId == underlying.Id &&
                value.IntervalSeconds == interval && value.Source == "Groww")
            .OrderByDescending(value => value.OpenTimeUtc).Take(40)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        if (candles.Count < 21) return;
        var latest = candles[^1];
        if (lastEvaluated.GetValueOrDefault(market.Code) == latest.OpenTimeUtc) return;
        lastEvaluated[market.Code] = latest.OpenTimeUtc;

        var decision = market == TradingMarketCatalog.NaturalGas
            ? EvaluateNaturalGas(candles)
            : Evaluate(candles);
        if (decision.Direction is null)
        {
            await AuditIfDueAsync(db, market, underlying.Id, latest.OpenTimeUtc, decision,
                cancellationToken);
            return;
        }

        if (market == TradingMarketCatalog.Sensex)
        {
            var reentryCutoff = now.AddMinutes(-20);
            var recentEntry = await db.MarketPaperPositions.AsNoTracking().AnyAsync(value =>
                value.Market == market.Code && value.OpenedAtUtc >= reentryCutoff,
                cancellationToken);
            if (recentEntry)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "ReentryCooldown",
                    decision.Confidence, ["Sensex 20-minute re-entry cooldown is active."], cancellationToken);
                return;
            }
        }

        if (market == TradingMarketCatalog.NaturalGas)
        {
            var monthStartLocal = new DateTime(india.Year, india.Month, 1);
            var monthStart = new DateTimeOffset(monthStartLocal,
                IndiaTimeZone.GetUtcOffset(monthStartLocal)).ToUniversalTime();
            var monthlyCalls = await db.MarketPaperPositions.AsNoTracking().CountAsync(value =>
                value.Market == market.Code && value.OpenedAtUtc >= monthStart, cancellationToken);
            if (monthlyCalls >= NaturalGasMiniPositionPolicy.MaximumCallsPerCalendarMonth)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "MonthlyCallLimit",
                    decision.Confidence, ["Natural Gas Mini positional call limit of three per calendar month is reached."],
                    cancellationToken);
                return;
            }
        }

        var execution = await SelectExecutionInstrumentAsync(db, market, decision.Direction.Value,
            latest.Close, today, cancellationToken);
        if (execution is null)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "InstrumentUnavailable",
                decision.Confidence, ["No eligible execution contract is synchronised."], cancellationToken);
            return;
        }
        if (await db.MarketPaperPositions.AnyAsync(value => value.Market == market.Code &&
                value.ExecutionInstrumentId == execution.Id && value.Status == "Active", cancellationToken))
            return;

        var quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
            execution.TradingSymbol), cancellationToken);
        var entry = market.ExecuteOptions ? quote.OfferPrice ?? quote.LastPrice : quote.LastPrice;
        if (entry <= 0) return;
        var tick = execution.TickSize > 0 ? execution.TickSize : 0.05m;
        var executionDirection = market.ExecuteOptions ? Direction.Buy : decision.Direction.Value;
        decimal stop;
        decimal target;
        int quantity;
        if (market == TradingMarketCatalog.NaturalGas)
        {
            if (!NaturalGasMiniPositionPolicy.IsSupportedContract(execution.LotSize))
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "ContractRejected",
                    decision.Confidence,
                    [$"Natural Gas Mini lot size is {execution.LotSize}; expected official lot size " +
                     $"{NaturalGasMiniPositionPolicy.ExpectedLotSize}. No call was issued."],
                    cancellationToken);
                return;
            }
            var recent = candles.TakeLast(12).ToArray();
            var atr = AverageTrueRange(recent);
            stop = executionDirection == Direction.Buy
                ? RoundToTick(recent.Min(value => value.Low) - atr * 0.25m, tick)
                : RoundToTick(recent.Max(value => value.High) + atr * 0.25m, tick);
            var structuralRisk = Math.Abs(entry - stop);
            target = RoundToTick(executionDirection == Direction.Buy
                ? entry + structuralRisk * 2m
                : entry - structuralRisk * 2m, tick);
            quantity = NaturalGasMiniPositionPolicy.FixedQuantity;
        }
        else
        {
            var riskDistance = entry * options.Value.OptionStopLossPercent / 100m;
            stop = RoundToTick(entry - riskDistance, tick);
            target = RoundToTick(entry + riskDistance, tick);
            var lotsByRisk = Math.Max(1, (int)Math.Floor(5000m /
                Math.Max(Math.Abs(entry - stop) * execution.LotSize, 0.01m)));
            quantity = execution.LotSize * Math.Min(lotsByRisk, options.Value.MaximumOptionLots);
        }
        var position = new MarketPaperPosition(Guid.NewGuid(), market.Code, underlying.Id,
            execution.Id, market == TradingMarketCatalog.NaturalGas
                ? $"Manual execution alert · {decision.Strategy}" : decision.Strategy,
            executionDirection, quantity, entry, stop, target, now);
        db.MarketPaperPositions.Add(position);
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id, latest.OpenTimeUtc,
            "PaperPositionOpened", decision.Confidence,
            JsonSerializer.Serialize(decision.Reasons.Append($"Execution: {execution.TradingSymbol}").ToArray())));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ManagePositionsAsync(TradingDbContext db, TradingMarketDefinition market,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = await db.MarketPaperPositions.Where(value => value.Market == market.Code &&
            value.Status == "Active").ToListAsync(cancellationToken);
        foreach (var position in active)
        {
            var instrument = await db.Instruments.AsNoTracking().SingleAsync(value =>
                value.Id == position.ExecutionInstrumentId, cancellationToken);
            var quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
                instrument.TradingSymbol), cancellationToken);
            var price = quote.LastPrice;
            if (price <= 0) continue;
            var initialRisk = Math.Abs(position.EntryPrice - position.StopLoss);
            var favourable = position.Direction == Direction.Buy ? price - position.EntryPrice : position.EntryPrice - price;
            var trailing = position.StopLoss;
            if (favourable >= initialRisk * 0.8m)
                trailing = position.Direction == Direction.Buy
                    ? position.EntryPrice + initialRisk * 0.5m
                    : position.EntryPrice - initialRisk * 0.5m;
            position.Mark(price, trailing);
            var stopHit = position.Direction == Direction.Buy ? price <= position.StopLoss : price >= position.StopLoss;
            var targetHit = position.Direction == Direction.Buy ? price >= position.Target : price <= position.Target;
            var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
            var forced = market != TradingMarketCatalog.NaturalGas &&
                         TimeOnly.FromDateTime(india.DateTime) >= market.SessionEnd.AddMinutes(-15);
            if (stopHit || targetHit || forced)
            {
                var costs = market.ExecuteOptions
                    ? PaperTradingCostModel.CalculateOptionCharges([
                        new(PaperOptionTransactionSide.Buy, position.EntryPrice, position.Quantity),
                        new(PaperOptionTransactionSide.Sell, price, position.Quantity)]).Total
                    : decimal.Round((position.EntryPrice + price) * position.Quantity * 0.0005m, 2);
                position.Close(price, costs, targetHit ? "TargetHit" : stopHit ? "StopLossHit" : "TimeExit", now);
            }
        }
        if (active.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }

    private static MarketDecision Evaluate(IReadOnlyList<Candle> candles)
    {
        var result = IndexMomentumBreakoutPolicy.Evaluate(candles);
        return new(result.Direction, result.Confidence, "Index momentum breakout", result.Reasons);
    }

    private static MarketDecision EvaluateNaturalGas(IReadOnlyList<Candle> candles)
    {
        var closes = candles.Select(value => value.Close).ToArray();
        var fast = TechnicalIndicators.ExponentialMovingAverage(closes, 9);
        var slow = TechnicalIndicators.ExponentialMovingAverage(closes, 21);
        var latest = candles[^1];
        var prior = candles.TakeLast(9).SkipLast(1).ToArray();
        var atr = AverageTrueRange(candles.TakeLast(15).ToArray());
        var impulse = Math.Abs(latest.Close - latest.Open) >= atr * 0.60m;
        var bullish = fast > slow && latest.Close > prior.Max(value => value.High) &&
                      latest.Close > latest.Open && impulse;
        var bearish = fast < slow && latest.Close < prior.Min(value => value.Low) &&
                      latest.Close < latest.Open && impulse;
        var separation = Math.Abs(fast - slow) / latest.Close;
        var confidence = Math.Min(0.90m, 0.55m + separation * 100m);
        if (bullish) return new(Direction.Buy, confidence, "Natural Gas Mini positional breakout",
            ["EMA 9 is above EMA 21.", "Close confirmed above eight-candle structure.",
             "Breakout candle has at least 0.60 ATR directional range."]);
        if (bearish) return new(Direction.Sell, confidence, "Natural Gas Mini positional breakout",
            ["EMA 9 is below EMA 21.", "Close confirmed below eight-candle structure.",
             "Breakout candle has at least 0.60 ATR directional range."]);
        return new(null, confidence, "Natural Gas Mini positional breakout",
            ["No confirmed EMA-aligned eight-candle positional breakout."]);
    }

    private static async Task<Instrument?> FindUnderlyingAsync(TradingDbContext db, TradingMarketDefinition market,
        DateOnly today, CancellationToken cancellationToken)
    {
        var query = db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
            value.Type == market.InstrumentType && value.IsActive);
        return market.InstrumentType == InstrumentType.Index
            ? await query.SingleOrDefaultAsync(value => value.TradingSymbol == market.UnderlyingSymbol,
                cancellationToken)
            : await query.Where(value => value.TradingSymbol.StartsWith(market.UnderlyingSymbol) &&
                                         value.ExpiryDate >= today)
                .OrderBy(value => value.ExpiryDate).ThenBy(value => value.TradingSymbol)
                .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<Instrument?> SelectExecutionInstrumentAsync(TradingDbContext db,
        TradingMarketDefinition market, Direction direction, decimal price, DateOnly today,
        CancellationToken cancellationToken)
    {
        if (!market.ExecuteOptions)
            return await db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
                    value.Type == InstrumentType.Future && value.TradingSymbol.StartsWith(market.ExecutionUnderlying) &&
                    value.ExpiryDate >= today && value.IsActive)
                .OrderBy(value => value.ExpiryDate).FirstOrDefaultAsync(cancellationToken);
        var instruments = await db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
                value.Segment == InstrumentSegment.FuturesAndOptions && value.IsActive && value.ExpiryDate >= today &&
                value.ExpiryDate <= today.AddDays(10) && value.StrikePrice != null &&
                (direction == Direction.Buy ? value.Type == InstrumentType.CallOption : value.Type == InstrumentType.PutOption) &&
                value.TradingSymbol.StartsWith(market.ExecutionUnderlying))
            .OrderBy(value => value.ExpiryDate).ThenBy(value => Math.Abs(value.StrikePrice!.Value - price))
            .FirstOrDefaultAsync(cancellationToken);
        return instruments;
    }

    private async Task AuditIfDueAsync(TradingDbContext db, TradingMarketDefinition market, Guid instrumentId,
        DateTimeOffset candleTime, MarketDecision decision, CancellationToken cancellationToken)
    {
        var cutoff = candleTime.AddMinutes(-options.Value.NoTradeAuditIntervalMinutes);
        if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value => value.Market == market.Code &&
            value.CandleTimeUtc >= cutoff, cancellationToken)) return;
        await AddAuditAsync(db, market, instrumentId, candleTime, "NoSignal", decision.Confidence,
            decision.Reasons, cancellationToken);
    }

    private static async Task AddAuditAsync(TradingDbContext db, TradingMarketDefinition market, Guid instrumentId,
        DateTimeOffset candleTime, string outcome, decimal confidence, IReadOnlyList<string> reasons,
        CancellationToken cancellationToken)
    {
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, instrumentId, candleTime,
            outcome, confidence, JsonSerializer.Serialize(reasons)));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static decimal RoundToTick(decimal value, decimal tick) =>
        Math.Max(tick, Math.Round(value / tick, MidpointRounding.AwayFromZero) * tick);
    private static decimal AverageTrueRange(Candle[] candles)
    {
        if (candles.Length < 2) throw new ArgumentException("At least two candles are required.", nameof(candles));
        var ranges = new List<decimal>(candles.Length - 1);
        for (var index = 1; index < candles.Length; index++)
            ranges.Add(Math.Max(candles[index].High - candles[index].Low,
                Math.Max(Math.Abs(candles[index].High - candles[index - 1].Close),
                    Math.Abs(candles[index].Low - candles[index - 1].Close))));
        return ranges.Average();
    }
    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }
    private sealed record MarketDecision(Direction? Direction, decimal Confidence, string Strategy,
        IReadOnlyList<string> Reasons);
    [LoggerMessage(Level = LogLevel.Error, Message = "{Market} paper automation cycle failed.")]
    private static partial void LogCycleFailed(ILogger logger, string market, Exception exception);
}
