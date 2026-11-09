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
using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.Infrastructure.Execution;

internal sealed partial class HeroZeroPaperTradingService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    HeroZeroMonitorState state,
    IOptions<HeroZeroOptions> options,
    IOptions<MarketDataOptions> marketData,
    TimeProvider timeProvider,
    TradingEngineEvidenceRecorder evidenceRecorder,
    LiveExecutionWakeSignal liveWake,
    ILogger<HeroZeroPaperTradingService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            foreach (var market in new[] { TradingMarketCatalog.Nifty, TradingMarketCatalog.Sensex })
                await evidenceRecorder.RecordAsync(market.Code, "hero-zero", "Startup", "Disabled",
                    "Hero Zero is disabled by server configuration.", timeProvider.GetUtcNow(),
                    new { options.Value.Enabled }, stoppingToken);
            return;
        }
        await Task.WhenAll(new[] { TradingMarketCatalog.Nifty, TradingMarketCatalog.Sensex }
            .Select(market => RunMarketLoopAsync(market, stoppingToken)));
    }

    private async Task RunMarketLoopAsync(TradingMarketDefinition market, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
                try { await ScanAsync(market, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    LogScanFailure(logger, market.Code, exception);
                    state.Set(state.GetSnapshot(market.Code) with { Status = "Faulted",
                        Explanation = "The latest scan failed; existing basket protection remains pending for retry.",
                        ObservedAtUtc = timeProvider.GetUtcNow() });
                    await evidenceRecorder.RecordAsync(market.Code, "hero-zero", "Scan", "Faulted",
                        "Hero Zero scan or position management failed.", timeProvider.GetUtcNow(),
                        TradingEngineEvidenceRecorder.FailureEvidence(exception), stoppingToken);
                }
            var seconds = state.GetSnapshot(market.Code).ActiveLegs.Count > 0
                ? options.Value.ManagementIntervalSeconds : options.Value.ScanIntervalSeconds;
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
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
        var today = DateOnly.FromDateTime(india.Date);
        var localTime = TimeOnly.FromDateTime(india.DateTime);
        var underlying = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Exchange == market.Exchange && value.Type == InstrumentType.Index &&
            value.TradingSymbol == market.UnderlyingSymbol && value.IsActive, cancellationToken);
        if (underlying is null)
        {
            await evidenceRecorder.RecordAsync(market.Code, "hero-zero", "Instrument", "InstrumentUnavailable",
                "The index instrument is not synchronised.", now,
                new { market.UnderlyingSymbol }, cancellationToken);
            state.Set(Empty(market.Code, "InstrumentUnavailable", "Synchronise the index instrument."));
            return;
        }

        var expiry = await db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
                value.IsActive && value.ExpiryDate >= today &&
                (value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption) &&
                value.TradingSymbol.StartsWith(market.ExecutionUnderlying))
            .MinAsync(value => value.ExpiryDate, cancellationToken);
        var active = await db.MarketPaperPositions.Where(value => value.Market == market.Code &&
            value.Status == "Active" && value.Strategy.StartsWith("Hero Zero|")).ToListAsync(cancellationToken);
        if (active.Count > 0)
        {
            var pairPrefix = string.Join('|', active[0].Strategy.Split('|').Take(2)) + "|";
            var pair = await db.MarketPaperPositions.Where(value => value.Market == market.Code &&
                value.Strategy.StartsWith(pairPrefix)).ToListAsync(cancellationToken);
            var pairExpiry = await db.Instruments.AsNoTracking().Where(value =>
                value.Id == active[0].ExecutionInstrumentId).Select(value => value.ExpiryDate)
                .SingleAsync(cancellationToken);
            await ManagePairAsync(db, market, pairExpiry ?? today, underlying.Id, pair, now,
                localTime, cancellationToken);
            return;
        }
        if (india.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
            localTime < market.SessionStart || localTime > market.SessionEnd)
        {
            state.Set(new(market.Code, expiry == today, expiry, "Market closed",
                "Hero Zero discovery resumes during the market session.", now,
                null, null, null, [], 0m, 0m, 0m));
            return;
        }
        if (expiry is null || expiry != today)
        {
            await evidenceRecorder.RecordAsync(market.Code, "hero-zero", "ExpirySelection", "NoCurrentExpiry",
                "No eligible current-expiry Hero Zero entry is available.", now,
                new { today, selectedExpiry = expiry }, cancellationToken);
            state.Set(new(market.Code, false, expiry, "Watching next expiry",
                expiry is null ? "No eligible weekly option expiry is synchronised." :
                    $"Next synchronised expiry is {expiry:dd MMM}; no Hero Zero entry today.",
                now, null, null, null, [], 0m, 0m, 0m));
            return;
        }

        var spotQuote = await gateway.GetQuoteAsync(new(market.Exchange, market.QuoteSegment,
            market.UnderlyingSymbol), cancellationToken);
        if (spotQuote.LastPrice <= 0)
        {
            await evidenceRecorder.RecordAsync(market.Code, "hero-zero", "SpotQuote", "QuoteUnavailable",
                "A positive index quote is required.", now,
                new { spotQuote.LastPrice }, cancellationToken);
            state.Set(Empty(market.Code, "DataUnavailable", "A fresh index quote is required."));
            return;
        }

        var discovery = await LoadCandidatesAsync(db, market, expiry.Value, spotQuote.LastPrice,
            cancellationToken);
        now = timeProvider.GetUtcNow();
        localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, IndiaTimeZone).DateTime);
        var candidates = discovery.Candidates;
        var targetPremium = market == TradingMarketCatalog.Sensex
            ? options.Value.SensexTargetPremium : options.Value.TargetPremium;
        var maximumStrikeDistance = market == TradingMarketCatalog.Sensex
            ? options.Value.SensexMaximumStrikeDistance
            : options.Value.NiftyMaximumStrikeDistance;
        var call = HeroZeroCandidatePolicy.Select(candidates, "CE", targetPremium,
            options.Value.MaximumSpreadPercent, spotQuote.LastPrice, maximumStrikeDistance);
        var put = HeroZeroCandidatePolicy.Select(candidates, "PE", targetPremium,
            options.Value.MaximumSpreadPercent, spotQuote.LastPrice, maximumStrikeDistance);
        var status = "Waiting for confirmation";
        var explanation = "Monitoring expiry expansion; the HZ button is view-only.";
        var candidateReady = call is not null && put is not null &&
                             call.Score >= options.Value.MinimumCandidateScore &&
                             put.Score >= options.Value.MinimumCandidateScore;
        var inEntryWindow = IndexEntryWindowPolicy.AllowsEntry(localTime,
            ParseTime(options.Value.EntryWindowStart), ParseTime(options.Value.EntryCutoff));
        var expansion = candidateReady && inEntryWindow
            ? await ExpansionTriggerAsync(db, underlying.Id, cancellationToken) : null;
        var depthReady = call?.MarketDepthAvailable == true && put?.MarketDepthAvailable == true;
        var canEnter = candidateReady && depthReady && inEntryWindow && expansion?.Permitted == true;
        var openedToday = await db.MarketPaperPositions.AsNoTracking().AnyAsync(value =>
            value.Market == market.Code && value.Strategy.StartsWith("Hero Zero|") &&
            value.OpenedAtUtc >= StartOfIndiaDay(now), cancellationToken);
        var finalEntryFresh = true;
        if (canEnter && !openedToday)
        {
            // Reprice only the selected legs and underlying. Discovery can take time;
            // never open against its old premiums or its old entry-window clock.
            var originalCallScore = call!.Score;
            var originalPutScore = put!.Score;
            var refreshed = await LoadCandidatesAsync(db, market, expiry.Value, spotQuote.LastPrice,
                cancellationToken, new HashSet<Guid> { call!.InstrumentId, put!.InstrumentId });
            var freshSpot = await gateway.GetQuoteAsync(new(market.Exchange, market.QuoteSegment,
                market.UnderlyingSymbol), cancellationToken);
            now = timeProvider.GetUtcNow();
            localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, IndiaTimeZone).DateTime);
            call = HeroZeroCandidatePolicy.Select(refreshed.Candidates, "CE", targetPremium,
                options.Value.MaximumSpreadPercent, freshSpot.LastPrice, maximumStrikeDistance);
            put = HeroZeroCandidatePolicy.Select(refreshed.Candidates, "PE", targetPremium,
                options.Value.MaximumSpreadPercent, freshSpot.LastPrice, maximumStrikeDistance);
            // A two-leg refresh must not improve relative OI/volume scores merely
            // because the other discovered contracts were not refreshed.
            if (call is not null) call = call with { Score = Math.Min(call.Score, originalCallScore) };
            if (put is not null) put = put with { Score = Math.Min(put.Score, originalPutScore) };
            finalEntryFresh = refreshed.Observations.All(value => value.ReceivedAtUtc is { } received &&
                HeroZeroCandidatePolicy.IsFreshQuote(value.SourceTime, received, now)) &&
                refreshed.Observations.Count == 2 && freshSpot.LastPrice > 0 &&
                HeroZeroCandidatePolicy.IsFreshQuote(freshSpot.LastTradeTimeEpochMilliseconds, now, now);
            candidateReady = call is not null && put is not null &&
                call.Score >= options.Value.MinimumCandidateScore && put.Score >= options.Value.MinimumCandidateScore;
            inEntryWindow = IndexEntryWindowPolicy.AllowsEntry(localTime,
                ParseTime(options.Value.EntryWindowStart), ParseTime(options.Value.EntryCutoff));
            expansion = await ExpansionTriggerAsync(db, underlying.Id, cancellationToken);
            now = timeProvider.GetUtcNow();
            localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, IndiaTimeZone).DateTime);
            inEntryWindow = IndexEntryWindowPolicy.AllowsEntry(localTime,
                ParseTime(options.Value.EntryWindowStart), ParseTime(options.Value.EntryCutoff));
            finalEntryFresh = finalEntryFresh && refreshed.Observations.All(value =>
                value.ReceivedAtUtc is { } received && HeroZeroCandidatePolicy.IsFreshQuote(value.SourceTime, received, now));
            depthReady = call?.MarketDepthAvailable == true && put?.MarketDepthAvailable == true;
            canEnter = candidateReady && depthReady && inEntryWindow && finalEntryFresh && expansion.Permitted;
            discovery.Observations.AddRange(refreshed.Observations);
        }
        HeroZeroPairBudgetDecision? pairBudget = null;
        var entryLots = configuredLots;
        if (candidateReady && market == TradingMarketCatalog.Sensex)
        {
            pairBudget = HeroZeroPairBudgetPolicy.Size(call!.Premium, put!.Premium,
                call.LotSize, put.LotSize, configuredLots);
            entryLots = pairBudget.Lots;
            canEnter &= entryLots > 0;
        }
        if (canEnter && !openedToday)
        {
            await OpenPairAsync(db, market, underlying.Id, call!, put!, entryLots, now,
                cancellationToken);
            scope.ServiceProvider.GetRequiredService<LiveExecutionWakeSignal>().Notify();
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
        else if (localTime >= ParseTime(options.Value.EntryCutoff))
            explanation = $"The {options.Value.EntryWindowStart}–{options.Value.EntryCutoff} IST Hero Zero entry window has closed.";
        else if (!finalEntryFresh)
        {
            status = "Waiting for fresh quotes";
            explanation = "Selected legs could not be revalidated with fresh quotes; no pair opened.";
        }
        else if (candidateReady && !depthReady)
        {
            status = "Waiting for market depth";
            explanation = "Candidates are visible, but fresh two-sided bid/ask evidence is required on both legs before entry. Unknown spread is not zero spread.";
        }
        else if (pairBudget is { Lots: 0 })
        {
            status = "Pair budget exceeded";
            explanation = "One whole Sensex CE+PE lot exceeds the ₹5,000 purchase-cost-plus-fees budget.";
        }
        else if (call is null || put is null)
        {
            status = "Waiting for pair";
            var missing = call is null && put is null ? "CE and PE" : call is null ? "CE" : "PE";
            var premiumLimit = market == TradingMarketCatalog.Sensex
                ? options.Value.SensexMaximumPremium : options.Value.MaximumPremium;
            explanation = $"No eligible {missing}: requires ₹{options.Value.MinimumPremium:0}–₹{premiumLimit:0} premium, liquid OTM contracts within {maximumStrikeDistance:0} index points. Per-contract rejection evidence is saved.";
        }
        else if (call.Score < options.Value.MinimumCandidateScore || put.Score < options.Value.MinimumCandidateScore)
        {
            status = "Waiting for quality";
            explanation = "Candidate liquidity/OI/volume quality is below the entry threshold.";
        }
        else
            explanation = $"Candidates are valid; waiting for a price-range expansion trigger. {string.Join(" ", expansion?.Reasons ?? Array.Empty<string>())}";

        state.Set(new(market.Code, true, expiry, status, explanation, now, spotQuote.LastPrice,
            call, put, [], 0m, 0m, 0m));
        await evidenceRecorder.RecordAsync(market.Code, "hero-zero", "EntryDecision", status,
            explanation, now, new { expiry, spotQuote.LastPrice, candidateReady,
                inEntryWindow, openedToday, call, put, expansion,
                options.Value.MinimumCandidateScore, finalEntryFresh, depthReady, pairBudget, entryLots, targetPremium,
                discovery.Observations }, cancellationToken);
        if (inEntryWindow && !openedToday)
        {
            // The 15-minute monitor audit can miss transient CE/PE pairs. Persist
            // each entry-window decision so a visible pair can be explained later.
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id, now,
                "ExpiryPairEntryDecision", Math.Min(call?.Score ?? 0m, put?.Score ?? 0m),
                JsonSerializer.Serialize(new { call = call?.Symbol, callScore = call?.Score,
                    put = put?.Symbol, putScore = put?.Score, candidateReady,
                    expansionPermitted = expansion?.Permitted, expansionReasons = expansion?.Reasons,
                    opened = canEnter, explanation, finalEntryFresh, depthReady, pairBudget, entryLots, targetPremium,
                    discovery.Observations })));
            await db.SaveChangesAsync(cancellationToken);
        }
        await AuditAsync(db, market.Code, underlying.Id, now, status, call, put, explanation,
            cancellationToken);
    }

    private sealed record CandidateObservation(string Symbol, decimal Strike, string OptionType,
        decimal? Premium, DateTimeOffset? ReceivedAtUtc, long? SourceTime, string Result, GrowwQuote? Quote);

    private sealed record CandidateDiscovery(List<HeroZeroCandidateInput> Candidates,
        List<CandidateObservation> Observations);

    private async Task<CandidateDiscovery> LoadCandidatesAsync(TradingDbContext db,
        TradingMarketDefinition market, DateOnly expiry, decimal spot, CancellationToken cancellationToken,
        HashSet<Guid>? selected = null)
    {
        var available = await db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
                value.IsActive && value.ExpiryDate == expiry && value.StrikePrice != null &&
                (value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption) &&
                value.TradingSymbol.StartsWith(market.ExecutionUnderlying))
            .OrderBy(value => Math.Abs(value.StrikePrice!.Value - spot))
            .ToListAsync(cancellationToken);
        var maximumDistance = market == TradingMarketCatalog.Sensex
            ? options.Value.SensexMaximumStrikeDistance : options.Value.NiftyMaximumStrikeDistance;
        // Apply the EXISTING strike eligibility before spending quote requests on contracts
        // that Select would always reject. Keep the shared gateway's rate limiter intact.
        available = available.Where(value => (selected is null || selected.Contains(value.Id)) &&
            HeroZeroCandidatePolicy.IsEligibleStrike(value.Type == InstrumentType.CallOption ? "CE" : "PE",
                value.StrikePrice!.Value, spot, maximumDistance)).ToList();
        // Guarantee equal discovery depth. A combined Take could silently crowd out CE or PE.
        var instruments = available.Where(value => value.Type == InstrumentType.CallOption)
            .Take(options.Value.NearbyContractsPerSide)
            .Concat(available.Where(value => value.Type == InstrumentType.PutOption)
                .Take(options.Value.NearbyContractsPerSide)).ToArray();
        var result = new List<HeroZeroCandidateInput>();
        var observations = new List<CandidateObservation>();
        var maximumPremium = market == TradingMarketCatalog.Sensex
            ? options.Value.SensexMaximumPremium : options.Value.MaximumPremium;
        foreach (var instrument in instruments)
        {
            var optionType = instrument.Type == InstrumentType.CallOption ? "CE" : "PE";
            GrowwQuote quote;
            try
            {
                quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
                    instrument.TradingSymbol), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                observations.Add(new(instrument.TradingSymbol, instrument.StrikePrice!.Value, optionType,
                    null, null, null, $"QuoteFailure:{exception.GetType().Name}", null));
                continue;
            }
            var receivedAt = timeProvider.GetUtcNow();
            var premium = quote.OfferPrice ?? quote.LastPrice;
            var reason = !HeroZeroCandidatePolicy.IsFreshQuote(quote.LastTradeTimeEpochMilliseconds, receivedAt, receivedAt)
                ? "StaleQuote" : premium < options.Value.MinimumPremium ? "BelowMinimumPremium" :
                premium > maximumPremium ? "AboveMaximumPremium" : "Candidate";
            observations.Add(new(instrument.TradingSymbol, instrument.StrikePrice!.Value, optionType,
                premium, receivedAt, quote.LastTradeTimeEpochMilliseconds, reason, quote));
            if (reason != "Candidate") continue;
            result.Add(new(instrument.Id, instrument.TradingSymbol, optionType,
                instrument.StrikePrice!.Value, expiry, instrument.LotSize, premium,
                quote.BidPrice ?? 0m, quote.OfferPrice ?? 0m, quote.Volume ?? 0m,
                quote.OpenInterest ?? 0m, quote.OpenInterestDayChange ?? 0m));
        }
        return new(result, observations);
    }

    private static async Task OpenPairAsync(TradingDbContext db, TradingMarketDefinition market, Guid underlyingId,
        HeroZeroCandidate call, HeroZeroCandidate put, int lots, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pairId = Guid.NewGuid().ToString("N");
        foreach (var candidate in new[] { call, put })
        {
            var tick = 0.05m;
            var stop = Math.Max(tick, decimal.Round(candidate.Premium * 0.20m / tick) * tick);
            var target = decimal.Round(candidate.Premium * 5m / tick) * tick;
            db.MarketPaperPositions.Add(new(Guid.NewGuid(), market.Code, underlyingId,
                candidate.InstrumentId, $"Hero Zero|{pairId}|{candidate.OptionType}", Direction.Buy,
                checked(candidate.LotSize * lots), candidate.Premium, stop, target, now));
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
        var quotes = new Dictionary<Guid, GrowwQuote>();
        var receivedAt = new Dictionary<Guid, DateTimeOffset>();
        var quoteTimes = new List<DateTimeOffset>();
        foreach (var leg in legs.Where(value => value.Status == "Active"))
        {
            var instrument = instruments[leg.ExecutionInstrumentId];
            GrowwQuote quote;
            try { quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
                instrument.TradingSymbol), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                await evidenceRecorder.RecordAsync(market.Code, "hero-zero", "BasketQuote", "Unavailable",
                    "An open leg quote failed; saved protection is retained and liquidation will be retried.",
                    timeProvider.GetUtcNow(), new { leg.Id, instrument.TradingSymbol, failure = exception.GetType().Name }, cancellationToken);
                continue;
            }
            var received = timeProvider.GetUtcNow();
            if (quote.LastPrice > 0 && HeroZeroCandidatePolicy.IsFreshQuote(quote.LastTradeTimeEpochMilliseconds, received, received))
            {
                quotes[leg.Id] = quote;
                receivedAt[leg.Id] = received;
                quoteTimes.Add(GrowwQuoteTimestamp.ToUtc(quote.LastTradeTimeEpochMilliseconds) ?? received);
                leg.Mark(quote.BidPrice is > 0m && quote.OfferPrice >= quote.BidPrice ? quote.BidPrice.Value : quote.LastPrice,
                    leg.StopLoss, received);
                if (await ManualTradeExitRequest.ExistsAsync(db, market.Code, leg.Id, cancellationToken))
                    Close(leg, "ManualExit");
            }
        }
        now = timeProvider.GetUtcNow();
        localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, IndiaTimeZone).DateTime);
        var entryCost = legs.Sum(value => value.EntryPrice * value.Quantity);
        var pairPrefix = string.Join('|', legs[0].Strategy.Split('|').Take(2));
        var outcome = "HeroZeroBasketState:" + pairPrefix;
        var previousJson = await db.MarketStrategyAudits.AsNoTracking().Where(a => a.Market == market.Code && a.Outcome == outcome)
            .OrderByDescending(a => a.CandleTimeUtc).Select(a => a.ReasonsJson).FirstOrDefaultAsync(cancellationToken);
        HeroZeroBasketProtectionState? previous = null;
        if (previousJson is not null)
        {
            try { previous = JsonSerializer.Deserialize<HeroZeroBasketProtectionState>(previousJson)
                ?? throw new JsonException(); }
            catch (JsonException) { previous = new(entryCost, 0m, 0m, false, "HeroZeroProtectionStateInvalid"); }
        }
        bool Fresh(MarketPaperPosition leg) => quotes.TryGetValue(leg.Id, out var quote) &&
            HeroZeroCandidatePolicy.IsFreshQuote(quote.LastTradeTimeEpochMilliseconds, receivedAt[leg.Id], now);
        var allFresh = legs.Where(l => l.Status == "Active").All(Fresh) &&
            (quoteTimes.Count == 0 || quoteTimes.Max() - quoteTimes.Min() <= TimeSpan.FromSeconds(5));
        var executableBook = allFresh && legs.Where(l => l.Status == "Active").All(l =>
            quotes[l.Id].BidPrice is > 0m && quotes[l.Id].OfferPrice >= quotes[l.Id].BidPrice);
        var net = allFresh ? legs.Sum(leg => leg.Status == "Active"
            ? leg.CurrentPnlAt(leg.CurrentPrice) - Charges(leg, leg.CurrentPrice) : leg.RealisedPnl) : (decimal?)null;
        var protection = HeroZeroBasketProtectionPolicy.Evaluate(entryCost, net, executableBook, previous,
            options.Value.CombinedStopLossPercent, options.Value.CombinedProfitActivationPercent,
            options.Value.WinnerTrailingFraction, localTime >= ParseTime(options.Value.ForcedExit),
            legs.Any(l => l.Status == "Active" && Fresh(l) && l.CurrentPrice <= l.StopLoss));
        // Commit the watermark/exit latch atomically with the paper closures.
        // Recovery must never reset an activated floor or cancel liquidation.
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlyingId, now,
            outcome, 1m, JsonSerializer.Serialize(protection)));
        if (protection.ExitReason is { } reason)
            foreach (var leg in legs.Where(l => l.Status == "Active" && Fresh(l))) Close(leg, reason);
        await db.SaveChangesAsync(cancellationToken);
        if (protection.ExitReason is not null || legs.Any(l => l.Status == "ManualExit")) liveWake.Notify();
        await evidenceRecorder.RecordAsync(market.Code, "hero-zero", "BasketProtection",
            protection.ExitReason ?? (allFresh ? "Managed" : "WaitingForFreshBasket"),
            protection.ExitReason is null ? "After-cost basket protection evaluated; individual red legs are not discarded."
                : "Basket liquidation latched; fresh legs closed and remaining legs will be retried.", now,
            new { pairPrefix, allFresh, executableBook, netLiquidationPnl = net, protection,
                legs = legs.Select(l => new { l.Id, l.CurrentPrice, l.RemainingQuantity, l.RealisedPnl, l.Status }),
                quotes = quotes.Select(q => new { positionId = q.Key, q.Value.LastPrice, q.Value.BidPrice, q.Value.OfferPrice,
                    q.Value.LastTradeTimeEpochMilliseconds }) }, cancellationToken);
        var currentValue = legs.Sum(value => value.CurrentPrice * value.RemainingQuantity);
        var combinedPnl = legs.Sum(value => value.CurrentPnlAt(value.CurrentPrice));
        var open = legs.Where(value => value.Status == "Active").Select(value =>
        {
            var instrument = instruments[value.ExecutionInstrumentId];
            return new HeroZeroLegSnapshot(value.Id, instrument.TradingSymbol,
                instrument.Type == InstrumentType.CallOption ? "CE" : "PE",
                instrument.StrikePrice ?? 0m, value.Quantity, value.EntryPrice, value.CurrentPrice,
                value.StopLoss, value.Status,
                value.CurrentPnlAt(value.CurrentPrice), null);
        }).ToArray();
        state.Set(new(market.Code, true, expiry, open.Length > 0 ? "Pair active" : "Pair closed",
            open.Length > 0 ? $"Basket protection active: {options.Value.CombinedStopLossPercent:0.#}% debit risk; after-cost floor ₹{protection.ProfitFloor:0.00}." :
                "The expiry pair has completed.", now, null, null, null, open,
            entryCost, currentValue, combinedPnl));

        void Close(MarketPaperPosition leg, string reason)
        {
            if (leg.Status != "Active") return;
            leg.Close(leg.CurrentPrice, Charges(leg, leg.CurrentPrice), reason, timeProvider.GetUtcNow());
        }

        decimal Charges(MarketPaperPosition leg, decimal price) => PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, leg.EntryPrice, leg.RemainingQuantity),
            new(PaperOptionTransactionSide.Sell, price, leg.RemainingQuantity)], market.Exchange).Total;
    }

    private async Task<HeroZeroExpansionDecision> ExpansionTriggerAsync(TradingDbContext db, Guid instrumentId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sessionStart = StartOfIndiaDay(now);
        var interval = marketData.Value.CandleIntervalSeconds;
        var candles = await db.Candles.AsNoTracking().Where(value => value.InstrumentId == instrumentId &&
                value.IntervalSeconds == interval && value.Source == "Groww" &&
                value.OpenTimeUtc >= sessionStart && value.OpenTimeUtc <= now.AddSeconds(-interval))
            .OrderByDescending(value => value.OpenTimeUtc).Take(15).OrderBy(value => value.OpenTimeUtc)
            .ToListAsync(cancellationToken);
        if (candles.Count == 0 || now - candles[^1].OpenTimeUtc > TimeSpan.FromSeconds(interval + 30))
            return new(false, null, ["Current-session completed expansion candle is unavailable or stale."]);
        return HeroZeroExpansionPolicy.Evaluate(candles.Select(value => new StrategyPriceBar(
            value.OpenTimeUtc, value.Open, value.High, value.Low, value.Close)).ToArray());
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
