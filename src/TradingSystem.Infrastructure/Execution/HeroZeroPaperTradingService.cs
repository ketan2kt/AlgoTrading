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

internal sealed partial class HeroZeroPaperTradingService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    HeroZeroMonitorState state,
    IOptions<HeroZeroOptions> options,
    IOptions<MarketDataOptions> marketData,
    TimeProvider timeProvider,
    ILogger<HeroZeroPaperTradingService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var market in new[] { TradingMarketCatalog.Nifty, TradingMarketCatalog.Sensex })
            {
                try { await ScanAsync(market, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    LogScanFailure(logger, market.Code, exception);
                    state.Set(new(market.Code, false, null, "Faulted",
                        "The latest scan failed safely; no new pair was opened.", timeProvider.GetUtcNow(),
                        null, null, null, [], 0m, 0m, 0m));
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.ScanIntervalSeconds), stoppingToken);
        }
    }

    private async Task ScanAsync(TradingMarketDefinition market, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var now = timeProvider.GetUtcNow();
        var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        var today = DateOnly.FromDateTime(india.Date);
        var localTime = TimeOnly.FromDateTime(india.DateTime);
        var underlying = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Exchange == market.Exchange && value.Type == InstrumentType.Index &&
            value.TradingSymbol == market.UnderlyingSymbol && value.IsActive, cancellationToken);
        if (underlying is null)
        {
            state.Set(Empty(market.Code, "InstrumentUnavailable", "Synchronise the index instrument."));
            return;
        }

        var expiry = await db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
                value.IsActive && value.ExpiryDate >= today &&
                (value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption) &&
                value.TradingSymbol.StartsWith(market.ExecutionUnderlying))
            .MinAsync(value => value.ExpiryDate, cancellationToken);
        if (expiry is null || expiry != today)
        {
            state.Set(new(market.Code, false, expiry, "Watching next expiry",
                expiry is null ? "No eligible weekly option expiry is synchronised." :
                    $"Next synchronised expiry is {expiry:dd MMM}; no Hero Zero entry today.",
                now, null, null, null, [], 0m, 0m, 0m));
            return;
        }

        var active = await db.MarketPaperPositions.Where(value => value.Market == market.Code &&
            value.Status == "Active" && value.Strategy.StartsWith("Hero Zero|")).ToListAsync(cancellationToken);
        if (active.Count > 0)
        {
            var pairPrefix = string.Join('|', active[0].Strategy.Split('|').Take(2)) + "|";
            var pair = await db.MarketPaperPositions.Where(value => value.Market == market.Code &&
                value.Strategy.StartsWith(pairPrefix)).ToListAsync(cancellationToken);
            await ManagePairAsync(db, market, expiry.Value, underlying.Id, pair, now, localTime, cancellationToken);
            return;
        }

        var spotQuote = await gateway.GetQuoteAsync(new(market.Exchange, market.QuoteSegment,
            market.UnderlyingSymbol), cancellationToken);
        if (spotQuote.LastPrice <= 0)
        {
            state.Set(Empty(market.Code, "DataUnavailable", "A fresh index quote is required."));
            return;
        }

        var candidates = await LoadCandidatesAsync(db, market, expiry.Value, spotQuote.LastPrice,
            cancellationToken);
        var call = HeroZeroCandidatePolicy.Select(candidates, "CE", options.Value.TargetPremium,
            options.Value.MaximumSpreadPercent);
        var put = HeroZeroCandidatePolicy.Select(candidates, "PE", options.Value.TargetPremium,
            options.Value.MaximumSpreadPercent);
        var status = "Candidates ready";
        var explanation = "Monitoring expiry expansion; the HZ button is view-only.";
        var canEnter = call is not null && put is not null &&
                       call.Score >= options.Value.MinimumCandidateScore &&
                       put.Score >= options.Value.MinimumCandidateScore &&
                       call.Premium + put.Premium <= options.Value.MaximumCombinedPremium &&
                       localTime >= ParseTime(options.Value.EntryWindowStart) &&
                       localTime <= ParseTime(options.Value.EntryCutoff) &&
                       await ExpansionTriggerAsync(db, underlying.Id, cancellationToken);
        var openedToday = await db.MarketPaperPositions.AsNoTracking().AnyAsync(value =>
            value.Market == market.Code && value.Strategy.StartsWith("Hero Zero|") &&
            value.OpenedAtUtc >= StartOfIndiaDay(now), cancellationToken);
        if (canEnter && !openedToday)
        {
            await OpenPairAsync(db, market, underlying.Id, call!, put!, now, cancellationToken);
            status = "Pair opened";
            explanation = "Liquidity-qualified CE and PE opened after confirmed expiry expansion.";
        }
        else if (openedToday)
        {
            status = "Completed for today";
            explanation = "One controlled Hero Zero pair has already been tested for this expiry session.";
        }
        else if (localTime < ParseTime(options.Value.EntryWindowStart))
            explanation = $"Candidates update automatically; entries begin after {options.Value.EntryWindowStart} IST.";
        else if (call is null || put is null)
            explanation = "Both liquid CE and PE candidates are not currently available.";
        else if (call.Score < options.Value.MinimumCandidateScore || put.Score < options.Value.MinimumCandidateScore)
            explanation = "Candidate liquidity/OI/volume quality is below the entry threshold.";
        else if (call.Premium + put.Premium > options.Value.MaximumCombinedPremium)
            explanation = "The combined debit is above the configured expiry-risk ceiling.";
        else
            explanation = "Candidates are valid; waiting for a price-range expansion trigger.";

        state.Set(new(market.Code, true, expiry, status, explanation, now, spotQuote.LastPrice,
            call, put, [], 0m, 0m, 0m));
        await AuditAsync(db, market.Code, underlying.Id, now, status, call, put, explanation,
            cancellationToken);
    }

    private async Task<List<HeroZeroCandidateInput>> LoadCandidatesAsync(TradingDbContext db,
        TradingMarketDefinition market, DateOnly expiry, decimal spot, CancellationToken cancellationToken)
    {
        var instruments = await db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
                value.IsActive && value.ExpiryDate == expiry && value.StrikePrice != null &&
                (value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption) &&
                value.TradingSymbol.StartsWith(market.ExecutionUnderlying))
            .OrderBy(value => Math.Abs(value.StrikePrice!.Value - spot))
            .Take(options.Value.NearbyContractsPerSide * 2).ToListAsync(cancellationToken);
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

    private static async Task OpenPairAsync(TradingDbContext db, TradingMarketDefinition market, Guid underlyingId,
        HeroZeroCandidate call, HeroZeroCandidate put, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pairId = Guid.NewGuid().ToString("N");
        foreach (var candidate in new[] { call, put })
        {
            var tick = 0.05m;
            var stop = Math.Max(tick, decimal.Round(candidate.Premium * 0.20m / tick) * tick);
            var target = decimal.Round(candidate.Premium * 5m / tick) * tick;
            db.MarketPaperPositions.Add(new(Guid.NewGuid(), market.Code, underlyingId,
                candidate.InstrumentId, $"Hero Zero|{pairId}|{candidate.OptionType}", Direction.Buy,
                candidate.LotSize, candidate.Premium, stop, target, now));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ManagePairAsync(TradingDbContext db, TradingMarketDefinition market, DateOnly expiry,
        Guid underlyingId, List<MarketPaperPosition> legs, DateTimeOffset now, TimeOnly localTime,
        CancellationToken cancellationToken)
    {
        var instruments = await db.Instruments.AsNoTracking().Where(value =>
            legs.Select(leg => leg.ExecutionInstrumentId).Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, cancellationToken);
        foreach (var leg in legs.Where(value => value.Status == "Active"))
        {
            var instrument = instruments[leg.ExecutionInstrumentId];
            var quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
                instrument.TradingSymbol), cancellationToken);
            if (quote.LastPrice > 0) leg.Mark(quote.LastPrice, leg.StopLoss);
        }
        var entryCost = legs.Sum(value => value.EntryPrice * value.Quantity);
        var currentValue = legs.Sum(value => value.CurrentPrice * value.Quantity);
        var combinedPnl = legs.Sum(value => value.Status == "Active"
            ? (value.CurrentPrice - value.EntryPrice) * value.Quantity
            : value.RealisedPnl);
        var force = localTime >= ParseTime(options.Value.ForcedExit);
        if (force || combinedPnl <= -entryCost * options.Value.CombinedStopLossPercent / 100m)
        {
            foreach (var leg in legs) Close(leg, force ? "HeroZeroTimeExit" : "HeroZeroCombinedStop");
        }
        else
        {
            var activeLegs = legs.Where(value => value.Status == "Active").ToArray();
            var winner = activeLegs.OrderByDescending(value => value.CurrentPrice / value.EntryPrice).First();
            if (winner.CurrentPrice >= winner.EntryPrice * options.Value.WinnerActivationMultiple)
            {
                var loser = activeLegs.SingleOrDefault(value => value.Id != winner.Id);
                if (loser is not null) Close(loser, "HeroZeroLosingLegExit");
                var trail = winner.CurrentPrice * (1m - options.Value.WinnerTrailingFraction);
                winner.Mark(winner.CurrentPrice, trail);
            }
            if (winner.CurrentPrice <= winner.StopLoss && winner.StopLoss > winner.EntryPrice)
                Close(winner, "HeroZeroTrailingStop");
        }
        await db.SaveChangesAsync(cancellationToken);
        var open = legs.Where(value => value.Status == "Active").Select(value =>
        {
            var instrument = instruments[value.ExecutionInstrumentId];
            return new HeroZeroLegSnapshot(value.Id, instrument.TradingSymbol,
                instrument.Type == InstrumentType.CallOption ? "CE" : "PE",
                instrument.StrikePrice ?? 0m, value.Quantity, value.EntryPrice, value.CurrentPrice,
                value.StopLoss, value.Status,
                (value.CurrentPrice - value.EntryPrice) * value.Quantity, null);
        }).ToArray();
        state.Set(new(market.Code, true, expiry, open.Length > 0 ? "Pair active" : "Pair closed",
            open.Length > 0 ? "Managing combined risk and trailing any confirmed winning leg." :
                "The expiry pair has completed.", now, null, null, null, open,
            entryCost, currentValue, combinedPnl));

        void Close(MarketPaperPosition leg, string reason)
        {
            if (leg.Status != "Active") return;
            var costs = PaperTradingCostModel.CalculateOptionCharges([
                new(PaperOptionTransactionSide.Buy, leg.EntryPrice, leg.Quantity),
                new(PaperOptionTransactionSide.Sell, leg.CurrentPrice, leg.Quantity)]).Total;
            leg.Close(leg.CurrentPrice, costs, reason, now);
        }
    }

    private async Task<bool> ExpansionTriggerAsync(TradingDbContext db, Guid instrumentId,
        CancellationToken cancellationToken)
    {
        var candles = await db.Candles.AsNoTracking().Where(value => value.InstrumentId == instrumentId &&
                value.IntervalSeconds == marketData.Value.CandleIntervalSeconds && value.Source == "Groww")
            .OrderByDescending(value => value.OpenTimeUtc).Take(15).OrderBy(value => value.OpenTimeUtc)
            .ToListAsync(cancellationToken);
        if (candles.Count < 10) return false;
        var ranges = candles.SkipLast(1).TakeLast(9).Select(value => value.High - value.Low).ToArray();
        var baseline = ranges.Average();
        var latest = candles[^1];
        var latestRange = latest.High - latest.Low;
        var latestBody = Math.Abs(latest.Close - latest.Open);
        return latestRange >= baseline * 0.80m && latestBody >= baseline * 0.45m;
    }

    private static async Task AuditAsync(TradingDbContext db, string market, Guid instrumentId,
        DateTimeOffset now, string status, HeroZeroCandidate? call, HeroZeroCandidate? put,
        string explanation, CancellationToken cancellationToken)
    {
        var recent = now.AddMinutes(-15);
        if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value => value.Market == market &&
            value.Outcome.StartsWith("HeroZero") && value.CandleTimeUtc >= recent, cancellationToken)) return;
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, instrumentId, now,
            $"HeroZero:{status}", Math.Min(call?.Score ?? 0m, put?.Score ?? 0m),
            JsonSerializer.Serialize(new { explanation, call, put })));
        await db.SaveChangesAsync(cancellationToken);
    }

    private HeroZeroMonitorSnapshot Empty(string market, string status, string explanation) =>
        new(market, false, null, status, explanation, timeProvider.GetUtcNow(), null, null, null,
            [], 0m, 0m, 0m);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Hero Zero scan failed for {Market}.")]
    private static partial void LogScanFailure(ILogger logger, string market, Exception exception);
}
