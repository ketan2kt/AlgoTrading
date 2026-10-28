using System.Collections.Concurrent;
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

internal sealed partial class IntradayExpansionPairService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    IOptions<IntradayExpansionPairOptions> options,
    IOptions<MarketDataOptions> marketData,
    TimeProvider timeProvider,
    ILogger<IntradayExpansionPairService> logger) : BackgroundService
{
    internal const string StrategyPrefix = "Expansion Pair|";
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    private readonly ConcurrentDictionary<string, decimal> _peakValues = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var market in new[] { TradingMarketCatalog.Nifty, TradingMarketCatalog.Sensex })
            {
                try { await ScanAsync(market, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) { LogScanFailure(logger, market.Code, exception); }
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.ScanIntervalSeconds), stoppingToken);
        }
    }

    private async Task ScanAsync(TradingMarketDefinition market, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var configuredLots = (await scope.ServiceProvider
            .GetRequiredService<ITradingLotConfigurationService>()
            .GetAsync(cancellationToken)).ForMarket(market.Code);
        var now = timeProvider.GetUtcNow();
        var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        var localTime = TimeOnly.FromDateTime(india.DateTime);
        var dayStart = StartOfIndiaDay(now);
        var underlying = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Exchange == market.Exchange && value.Type == InstrumentType.Index &&
            value.TradingSymbol == market.UnderlyingSymbol && value.IsActive, cancellationToken);
        if (underlying is null) return;

        var active = await db.MarketPaperPositions.Where(value => value.Market == market.Code &&
            value.Status == "Active" && value.Strategy.StartsWith(StrategyPrefix))
            .ToListAsync(cancellationToken);
        if (active.Count > 0)
        {
            var pairPrefix = PairPrefix(active[0].Strategy);
            var pair = await db.MarketPaperPositions.Where(value => value.Market == market.Code &&
                value.Strategy.StartsWith(pairPrefix)).ToListAsync(cancellationToken);
            await ManagePairAsync(db, market, underlying.Id, pairPrefix, pair, now, localTime,
                cancellationToken);
            return;
        }

        if (!IndexEntryWindowPolicy.AllowsEntry(localTime, ParseTime(options.Value.EntryWindowStart),
                ParseTime(options.Value.EntryCutoff))) return;

        var todayPositions = await db.MarketPaperPositions.AsNoTracking().Where(value =>
            value.Market == market.Code && value.OpenedAtUtc >= dayStart &&
            value.Strategy.StartsWith(StrategyPrefix)).ToListAsync(cancellationToken);
        var pairsToday = todayPositions.Select(value => PairPrefix(value.Strategy)).Distinct().Count();
        if (pairsToday >= options.Value.MaximumPairsPerDay) return;
        DateTimeOffset? lastOpened = todayPositions.Count == 0
            ? null : todayPositions.Max(value => value.OpenedAtUtc);
        if (lastOpened is not null && now - lastOpened < TimeSpan.FromMinutes(options.Value.CooldownMinutes)) return;

        var completedBefore = now.AddSeconds(-marketData.Value.CandleIntervalSeconds);
        var candles = await db.Candles.AsNoTracking().Where(value => value.InstrumentId == underlying.Id &&
                value.IntervalSeconds == marketData.Value.CandleIntervalSeconds && value.Source == "Groww" &&
                value.OpenTimeUtc >= dayStart && value.OpenTimeUtc <= completedBefore)
            .OrderByDescending(value => value.OpenTimeUtc).Take(90)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        if (candles.Count == 0) return;
        var fiveMinuteBars = IntradayExpansionPairPolicy.CompletedFiveMinuteBars(candles.Select(value =>
            new StrategyPriceBar(value.OpenTimeUtc, value.Open, value.High, value.Low, value.Close)).ToArray(),
            marketData.Value.CandleIntervalSeconds, now);
        if (fiveMinuteBars.Length == 0) return;
        var decision = IntradayExpansionPairPolicy.Evaluate(fiveMinuteBars);
        var decisionTime = fiveMinuteBars[^1].OpenTimeUtc;
        if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value => value.Market == market.Code &&
            value.CandleTimeUtc == decisionTime && value.Outcome.StartsWith("ExpansionPair:"), cancellationToken)) return;
        if (!decision.Permitted)
        {
            await AuditAsync(db, market.Code, underlying.Id, decisionTime, "ExpansionPair:Rejected",
                decision.Confidence, new { decision.Reasons, decision.CompressionRatio, decision.ReleaseRatio },
                cancellationToken);
            return;
        }

        var releaseDirection = fiveMinuteBars[^1].Close >= fiveMinuteBars[^1].Open
            ? Direction.Buy : Direction.Sell;
        var optionInterest = await OptionOpenInterestEntryContext.EvaluateAsync(db, underlying.Id,
            releaseDirection, fiveMinuteBars[^1].Close, Math.Max(decision.BaselineRange, .01m), now,
            cancellationToken);
        if (!optionInterest.Permitted)
        {
            await AuditAsync(db, market.Code, underlying.Id, decisionTime, "ExpansionPair:OiBarrierRejected",
                decision.Confidence, new { entryReasons = decision.Reasons,
                    oiReasons = optionInterest.Reasons, optionInterest.BarrierStrike,
                    optionInterest.BarrierKind, optionInterest.RoomToRisk }, cancellationToken);
            return;
        }

        var today = DateOnly.FromDateTime(india.Date);
        var expiry = await db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
                value.IsActive && value.ExpiryDate > today &&
                (value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption) &&
                value.TradingSymbol.StartsWith(market.ExecutionUnderlying))
            .MinAsync(value => value.ExpiryDate, cancellationToken);
        if (expiry is null)
        {
            await AuditAsync(db, market.Code, underlying.Id, decisionTime, "ExpansionPair:Unavailable",
                decision.Confidence, new { decision.Reasons, detail = "No non-expiry-day option series is synchronised." }, cancellationToken);
            return;
        }

        var spotQuote = await gateway.GetQuoteAsync(new(market.Exchange, market.QuoteSegment,
            market.UnderlyingSymbol), cancellationToken);
        if (spotQuote.LastPrice <= 0) return;
        var candidates = await LoadCandidatesAsync(db, market, expiry.Value, spotQuote.LastPrice,
            cancellationToken);
        var call = HeroZeroCandidatePolicy.Select(candidates, "CE", options.Value.TargetPremium,
            options.Value.MaximumSpreadPercent);
        var put = HeroZeroCandidatePolicy.Select(candidates, "PE", options.Value.TargetPremium,
            options.Value.MaximumSpreadPercent);
        if (call is null || put is null)
        {
            await AuditAsync(db, market.Code, underlying.Id, decisionTime, "ExpansionPair:Unavailable",
                decision.Confidence, new { decision.Reasons, detail = "A liquid CE/PE pair was unavailable." }, cancellationToken);
            return;
        }
        var candidateDecision = ExpansionPairCandidatePolicy.Evaluate(call, put,
            decimal.MaxValue, options.Value.MaximumPremiumImbalance,
            (call.Premium + put.Premium) * call.LotSize * options.Value.BasketStopPercent * configuredLots,
            options.Value.BasketStopPercent,
            configuredLots);
        if (!candidateDecision.Permitted || candidateDecision.Lots != configuredLots)
        {
            await AuditAsync(db, market.Code, underlying.Id, decisionTime, "ExpansionPair:Rejected",
                decision.Confidence, new { entryReasons = decision.Reasons,
                    candidateReasons = candidateDecision.Reasons,
                    configuredLots,
                    detail = candidateDecision.Permitted
                        ? "The configured size exceeds the current basket-risk approval; no smaller pair was opened."
                        : "The configured pair did not pass basket-risk approval.",
                    call, put }, cancellationToken);
            return;
        }

        await OpenPairAsync(db, market, underlying.Id, call, put, candidateDecision.Lots, now,
            decisionTime, decision, candidateDecision, optionInterest, cancellationToken);
    }

    private async Task<List<HeroZeroCandidateInput>> LoadCandidatesAsync(TradingDbContext db,
        TradingMarketDefinition market, DateOnly expiry, decimal spot, CancellationToken cancellationToken)
    {
        var available = await db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
                value.IsActive && value.ExpiryDate == expiry && value.StrikePrice != null &&
                (value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption) &&
                value.TradingSymbol.StartsWith(market.ExecutionUnderlying))
            .OrderBy(value => Math.Abs(value.StrikePrice!.Value - spot)).ToListAsync(cancellationToken);
        var instruments = available.Where(value => value.Type == InstrumentType.CallOption)
            .Take(options.Value.NearbyContractsPerSide)
            .Concat(available.Where(value => value.Type == InstrumentType.PutOption)
                .Take(options.Value.NearbyContractsPerSide)).ToArray();
        var result = new List<HeroZeroCandidateInput>();
        foreach (var instrument in instruments)
        {
            var quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
                instrument.TradingSymbol), cancellationToken);
            var premium = quote.OfferPrice ?? quote.LastPrice;
            if (premium < options.Value.MinimumPremium || premium > options.Value.MaximumPremium) continue;
            result.Add(new(instrument.Id, instrument.TradingSymbol,
                instrument.Type == InstrumentType.CallOption ? "CE" : "PE",
                instrument.StrikePrice!.Value, expiry, instrument.LotSize, premium,
                quote.BidPrice ?? 0m, quote.OfferPrice ?? 0m, quote.Volume ?? 0m,
                quote.OpenInterest ?? 0m, quote.OpenInterestDayChange ?? 0m));
        }
        return result;
    }

    private static async Task OpenPairAsync(TradingDbContext db, TradingMarketDefinition market,
        Guid underlyingId, HeroZeroCandidate call, HeroZeroCandidate put, int lots,
        DateTimeOffset now, DateTimeOffset decisionTime, ExpansionPairEntryDecision entryDecision,
        ExpansionPairCandidateDecision candidateDecision,
        OptionOpenInterestEntryDecision optionInterest, CancellationToken cancellationToken)
    {
        var pairId = Guid.NewGuid().ToString("N");
        foreach (var candidate in new[] { call, put })
        {
            var quantity = checked(candidate.LotSize * lots);
            db.MarketPaperPositions.Add(new(Guid.NewGuid(), market.Code, underlyingId,
                candidate.InstrumentId, $"{StrategyPrefix}{pairId}|{candidate.OptionType}", Direction.Buy,
                quantity, candidate.Premium, ExpansionPairExitPolicy.LegFloorStop,
                candidate.Premium * 10m, now));
        }
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlyingId, decisionTime,
            "ExpansionPair:Opened", entryDecision.Confidence, JsonSerializer.Serialize(new
            {
                pairId, entryReasons = entryDecision.Reasons,
                candidateReasons = candidateDecision.Reasons,
                oiAvailable = optionInterest.Available,
                oiReasons = optionInterest.Reasons,
                optionInterest.BarrierStrike, optionInterest.BarrierKind, optionInterest.RoomToRisk,
                candidateDecision.CombinedPremium, candidateDecision.EstimatedRisk,
                call = new { call.Symbol, call.Strike, call.Premium, call.Score, call.OpenInterest, call.OpenInterestChange },
                put = new { put.Symbol, put.Strike, put.Premium, put.Score, put.OpenInterest, put.OpenInterestChange }
            })));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ManagePairAsync(TradingDbContext db, TradingMarketDefinition market,
        Guid underlyingId, string pairPrefix, List<MarketPaperPosition> legs, DateTimeOffset now,
        TimeOnly localTime, CancellationToken cancellationToken)
    {
        var ids = legs.Select(value => value.ExecutionInstrumentId).ToArray();
        var instruments = await db.Instruments.AsNoTracking().Where(value => ids.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, cancellationToken);
        var manuallyClosedLeg = false;
        foreach (var leg in legs.Where(value => value.Status == "Active"))
        {
            var instrument = instruments[leg.ExecutionInstrumentId];
            var quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
                instrument.TradingSymbol), cancellationToken);
            if (quote.LastPrice <= 0m) continue;
            leg.Mark(quote.LastPrice, leg.StopLoss, now);
            if (await ManualTradeExitRequest.ExistsAsync(db, market.Code, leg.Id, cancellationToken))
            {
                leg.Close(quote.LastPrice, Charges(leg, quote.LastPrice, leg.RemainingQuantity),
                    "ManualExit", now);
                manuallyClosedLeg = true;
            }
            else if (ExpansionPairExitPolicy.ShouldStopLeg(leg))
                leg.Close(quote.LastPrice, Charges(leg, quote.LastPrice, leg.RemainingQuantity),
                    "ExpansionPairLegStop", now);
        }

        if (manuallyClosedLeg)
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var key = market.Code + ":" + pairPrefix;
        if (legs.All(value => value.Status != "Active"))
        {
            _peakValues.TryRemove(key, out _);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var entryValue = legs.Sum(value => value.EntryPrice * value.Quantity);
        var recoveryTaken = legs.Any(value => value.PartialExitQuantity > 0 ||
            value.Status == "ExpansionPairRecovery");
        var currentValue = recoveryTaken
            ? legs.Where(value => value.Status == "Active")
                .Sum(value => value.CurrentPrice * value.RemainingQuantity)
            : ExpansionPairExitPolicy.MarkedBasketValue(legs);
        var peak = _peakValues.AddOrUpdate(key, currentValue, (_, prior) => Math.Max(prior, currentValue));
        var action = ExpansionPairExitPolicy.Evaluate(entryValue, currentValue, peak,
            now - legs.Min(value => value.OpenedAtUtc), localTime, recoveryTaken,
            options.Value.BasketStopPercent, options.Value.MinimumExpansionPercent,
            options.Value.RunnerTrailFraction, ParseTime(options.Value.ForcedExit));

        if (action == ExpansionPairExitAction.RecoverAndRun)
        {
            var active = legs.Where(value => value.Status == "Active")
                .OrderByDescending(value => (value.CurrentPrice - value.EntryPrice) * value.RemainingQuantity)
                .ToArray();
            var winner = active.FirstOrDefault();
            var loser = active.Skip(1).FirstOrDefault();
            if (winner is not null && loser is not null)
            {
                var lotSize = instruments[winner.ExecutionInstrumentId].LotSize;
                var recoveryQuantity = (int)Math.Ceiling(entryValue / winner.CurrentPrice / lotSize) * lotSize;
                if (recoveryQuantity > 0 && recoveryQuantity <= winner.RemainingQuantity - lotSize)
                {
                    Close(loser, "ExpansionPairRecovery");
                    winner.TakePartial(winner.CurrentPrice, recoveryQuantity,
                        Charges(winner, winner.CurrentPrice, recoveryQuantity), now);
                    _peakValues[key] = winner.CurrentPrice * winner.RemainingQuantity;
                    await AuditAsync(db, market.Code, underlyingId, now, "ExpansionPair:Recovered",
                        1m, new { pairPrefix, entryValue, currentValue, winner = winner.Id,
                            loser = loser.Id, recoveryQuantity }, cancellationToken, save: false);
                }
            }
        }
        else if (action is ExpansionPairExitAction.StopBasket or ExpansionPairExitAction.TimeExit or
                 ExpansionPairExitAction.ForceExit or ExpansionPairExitAction.TrailRunner or
                 ExpansionPairExitAction.ProtectBasketProfit)
        {
            var status = action switch
            {
                ExpansionPairExitAction.StopBasket => "ExpansionPairStop",
                ExpansionPairExitAction.TimeExit => "ExpansionPairNoExpansion",
                ExpansionPairExitAction.ForceExit => "ExpansionPairTimeExit",
                ExpansionPairExitAction.ProtectBasketProfit => "ExpansionPairProfitProtection",
                _ => "ExpansionPairRunnerTrail"
            };
            foreach (var leg in legs) Close(leg, status);
            _peakValues.TryRemove(key, out _);
            await AuditAsync(db, market.Code, underlyingId, now, $"ExpansionPair:{action}",
                1m, new { pairPrefix, entryValue, currentValue, peakValue = peak },
                cancellationToken, save: false);
        }
        await db.SaveChangesAsync(cancellationToken);

        void Close(MarketPaperPosition leg, string status)
        {
            if (leg.Status != "Active") return;
            leg.Close(leg.CurrentPrice, Charges(leg, leg.CurrentPrice, leg.RemainingQuantity), status, now);
        }
    }

    private static decimal Charges(MarketPaperPosition leg, decimal exitPrice, int quantity) =>
        PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, leg.EntryPrice, quantity),
            new(PaperOptionTransactionSide.Sell, exitPrice, quantity)]).Total;

    private static async Task AuditAsync(TradingDbContext db, string market, Guid underlyingId,
        DateTimeOffset candleTime, string outcome, decimal confidence, object details,
        CancellationToken cancellationToken, bool save = true)
    {
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, underlyingId, candleTime,
            outcome, confidence, JsonSerializer.Serialize(details)));
        if (save) await db.SaveChangesAsync(cancellationToken);
    }

    private static string PairPrefix(string strategy) =>
        string.Join('|', strategy.Split('|').Take(2)) + "|";
    private static TimeOnly ParseTime(string value) => TimeOnly.ParseExact(value, "HH:mm");
    private static DateTimeOffset StartOfIndiaDay(DateTimeOffset now)
    {
        var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        return new DateTimeOffset(india.Year, india.Month, india.Day, 0, 0, 0,
            IndiaTimeZone.GetUtcOffset(india.Date)).ToUniversalTime();
    }
    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Intraday expansion-pair scan failed for {Market}.")]
    private static partial void LogScanFailure(ILogger logger, string market, Exception exception);
}
