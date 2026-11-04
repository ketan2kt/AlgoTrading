using System.Text.Json;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalNiftyExitSettings(
    TimeOnly ForcedExitTime,
    bool BreakEvenEnabled,
    bool TrailingStopEnabled,
    bool PartialProfitEnabled,
    bool TargetExtensionEnabled,
    decimal TargetExtensionMinimumConfidence,
    bool UnderlyingTrendInvalidationEnabled,
    decimal MinimumReversalStructureStrength,
    int RequiredReversalEvidenceCount,
    bool ProtectProfitableConfirmedReversals,
    decimal ReversalProfitLockFraction,
    int MaximumOptionQuoteAgeSeconds,
    int MaximumUnderlyingQuoteAgeSeconds,
    int CandleIntervalSeconds);

// Replays the production's pure Nifty stop, target, confirmed-reversal,
// failed-setup and partial decisions on timestamped observations. This is not
// normal intraday decision parity. The factory/control wrapper rejects overnight
// carry; coverage and non-executable partials independently gate any verdict.
public sealed class HistoricalNiftyPositionPolicy(
    IHistoricalReplayPortfolioPolicy entryPolicy,
    HistoricalNiftyExitSettings settings,
    string spotSymbol = "NIFTY", IReadOnlyDictionary<string, decimal>? ticks = null) : IHistoricalReplayPortfolioPolicy
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
    public string CodeVersion => entryPolicy.CodeVersion + "/nifty-exit-v7";
    public bool ProductionPolicyParityVerified => entryPolicy.ProductionPolicyParityVerified && settings.CandleIntervalSeconds is 60 or 300;
    public string ConfigurationManifest => entryPolicy.ConfigurationManifest +
        "|niftyExit=" + JsonSerializer.Serialize(settings);

    public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
        HistoricalReplayPortfolioFrame frame) => entryPolicy.Evaluate(frame)
            .Select(value => value with { RecomputesProtectionFromInitialRisk = true }).ToArray();

    public HistoricalReplayCandidate? AssessDeferredEntry(
        HistoricalReplayPortfolioFrame frame,
        HistoricalReplayCandidate pending,
        HistoricalReplayQuote optionQuote) =>
        entryPolicy.AssessDeferredEntry(frame, pending, optionQuote);

    public bool ShouldExtendTarget(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayOpenPosition position)
    {
        if (!settings.TargetExtensionEnabled ||
            !NiftyPositions(frame).Any(value => value.Symbol == position.Symbol) ||
            !TryOptionMark(frame, position, out _)) return false;
        var invalidation = Invalidation(frame, position);
        return ExitPlan().ExtendTarget && !invalidation.ShouldExit &&
            invalidation.Behaviour == SessionBehaviour.Trending &&
            position.SignalConfidence >= settings.TargetExtensionMinimumConfidence;
    }

    public IReadOnlyList<HistoricalReplayStopUpdate> EvaluateStopUpdates(
        HistoricalReplayPortfolioFrame frame)
    {
        var updates = new List<HistoricalReplayStopUpdate>();
        foreach (var position in NiftyPositions(frame))
        {
            if (!TryOptionMark(frame, position, out var mark)) continue;
            var favourable = Favourable(position, mark);
            var plan = ExitPlan();
            var quantity = position.OriginalQuantity > 0
                ? position.OriginalQuantity : position.Quantity;
            var proposed = PaperProtectiveStopPolicy.CalculateNetProtected(
                Direction.Buy, position.EntryPrice,
                position.InitialStopPrice, favourable, plan,
                settings.BreakEvenEnabled, settings.TrailingStopEnabled,
                quantity, position.BaseQuantity > 0
                    ? position.BaseQuantity : quantity, "NSE");
            var invalidation = Invalidation(frame, position);
            if (invalidation.ShouldExit && mark > position.EntryPrice &&
                settings.ProtectProfitableConfirmedReversals)
                proposed = PaperProtectiveStopPolicy.ProtectReversalProfit(
                    Direction.Buy, position.EntryPrice, mark, proposed,
                    settings.ReversalProfitLockFraction);
            proposed = ticks is not null ? PaperProtectiveStopPolicy.ExecutableTrail(Direction.Buy,
                proposed, position.StopPrice, ticks[position.Symbol]) : Math.Max(proposed, position.StopPrice);
            if (proposed != position.StopPrice)
                updates.Add(new(position.Symbol,
                    frame.Market.AvailableAtUtc, proposed) { RecomputedFromInitialRisk = true });
        }
        return updates;
    }

    public IReadOnlyList<HistoricalReplayExitRequest> EvaluateExits(
        HistoricalReplayPortfolioFrame frame)
    {
        var exits = new List<HistoricalReplayExitRequest>();
        foreach (var position in NiftyPositions(frame))
        {
            if (!TryOptionMark(frame, position, out _)) continue;
            var reason = Decision(frame, position).ExitReason;
            if (reason != PaperExitReason.None)
                exits.Add(new(position.Symbol, frame.Market.AvailableAtUtc,
                    reason.ToString()));
        }
        return exits;
    }

    public IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(
        HistoricalReplayPortfolioFrame frame)
    {
        var partials = new List<HistoricalReplayPartialExit>();
        foreach (var position in NiftyPositions(frame))
        {
            if (!TryOptionMark(frame, position, out _) ||
                position.Quantity != position.OriginalQuantity) continue;
            var decision = Decision(frame, position);
            if (decision.ExitReason != PaperExitReason.None ||
                decision.PartialQuantity <= 0) continue;
            var lotSize = position.LotSize;
            partials.Add(new(position.Symbol, frame.Market.AvailableAtUtc,
                decision.PartialQuantity, "PartialProfit")
            {
                NonExecutablePaperQuantity = lotSize is not > 0 ||
                    decision.PartialQuantity % lotSize.Value != 0
            });
        }
        return partials;
    }

    public IReadOnlyList<HistoricalReplayAddOn> EvaluateAddOns(
        HistoricalReplayPortfolioFrame frame)
    {
        var addOns = new List<HistoricalReplayAddOn>();
        foreach (var position in NiftyPositions(frame))
        {
            if (position.AddOnQuantity > 0 ||
                position.Quantity != position.OriginalQuantity ||
                position.LotSize is not > 0 ||
                position.SignalConfidence < .72m ||
                !TryOptionMark(frame, position, out var mark) ||
                mark <= position.StopPrice || mark < position.TargetPrice ||
                !ShouldExtendTarget(frame, position)) continue;
            var initialRisk = position.EntryPrice - position.InitialStopPrice;
            if (initialRisk <= 0m) continue;
            var favourableR = (Favourable(position, mark) -
                position.EntryPrice) / initialRisk;
            var proposedRisk = Math.Max(0m, mark - position.StopPrice) *
                position.LotSize.Value;
            var decision = WinnerAddOnPolicy.Evaluate(favourableR, true,
                false, initialRisk * position.OriginalQuantity,
                proposedRisk);
            if (decision.Permitted)
                addOns.Add(new(position.Symbol, frame.Market.AvailableAtUtc,
                    position.LotSize.Value, decision.MaximumAdditionalRisk,
                    "WinnerAddOn"));
        }
        return addOns;
    }

    private (PaperExitReason ExitReason, int PartialQuantity) Decision(
        HistoricalReplayPortfolioFrame frame, HistoricalReplayOpenPosition position)
    {
        if (!TryOptionMark(frame, position, out var mark))
            return (PaperExitReason.None, 0);
        var plan = ExitPlan();
        var invalidation = Invalidation(frame, position);
        var reversalProtected = invalidation.ShouldExit &&
            mark > position.EntryPrice &&
            settings.ProtectProfitableConfirmedReversals;
        var confirmedInvalidation = invalidation.ShouldExit && !reversalProtected;
        var extendTarget = settings.TargetExtensionEnabled &&
            plan.ExtendTarget && !confirmedInvalidation &&
            invalidation.Behaviour == SessionBehaviour.Trending &&
            position.SignalConfidence >= settings.TargetExtensionMinimumConfidence;
        var failedSetup = false;
        if (TryUnderlyingMark(frame, out var underlyingPrice) &&
            position.UnderlyingDirection is { } direction)
        {
            // Production selects the latest 30 completed candles, then
            // orders that slice oldest to newest for the failed-setup rule.
            var bars = SessionBars(frame).TakeLast(30).ToArray();
            var favourable = Favourable(position, mark);
            failedSetup = FailedSetupExitPolicy.Evaluate(direction,
                position.EntryPrice, mark,
                Math.Abs(position.EntryPrice - position.InitialStopPrice),
                favourable, position.DecisionAtUtc,
                frame.Market.AvailableAtUtc, underlyingPrice,
                bars).ShouldExit;
        }
        var reason = NiftyOpenPositionDecisionPolicy.ResolveExit(
            Direction.Buy, mark, position.StopPrice, position.TargetPrice,
            TimeOnly.FromDateTime(frame.Market.AvailableAtUtc.ToOffset(
                IndiaOffset).DateTime), settings.ForcedExitTime,
            true, extendTarget, false, false,
            confirmedInvalidation, failedSetup, false);
        var partial = NiftyOpenPositionDecisionPolicy.PartialQuantity(
            Direction.Buy, mark, position.EntryPrice,
            position.InitialStopPrice,
            position.OriginalQuantity > 0 ? position.OriginalQuantity : position.Quantity,
            position.LotSize ?? 0, plan, settings.PartialProfitEnabled,
            position.Quantity != position.OriginalQuantity, reason);
        return (reason, partial);
    }

    private UnderlyingTrendInvalidationResult Invalidation(
        HistoricalReplayPortfolioFrame frame, HistoricalReplayOpenPosition position)
    {
        if (!settings.UnderlyingTrendInvalidationEnabled ||
            position.UnderlyingDirection is not { } direction ||
            !TryUnderlyingMark(frame, out _))
            return new(false, 0m, ["Fresh underlying observation unavailable."]);
        var bars = SessionBars(frame);
        if (bars.Length < 21 || bars.Count(value =>
                value.OpenTimeUtc.AddSeconds(settings.CandleIntervalSeconds) > position.DecisionAtUtc) < 2)
            return new(false, 0m, ["Completed post-entry underlying evidence is insufficient."]);
        var closes = bars.Select(value => value.Close).ToArray();
        return UnderlyingTrendInvalidationPolicy.Evaluate(direction, bars,
            Ema(closes, 9), Ema(closes, 21),
            settings.MinimumReversalStructureStrength,
            settings.RequiredReversalEvidenceCount);
    }

    private StrategyPriceBar[] SessionBars(HistoricalReplayPortfolioFrame frame)
    {
        var date = DateOnly.FromDateTime(
            frame.Market.AvailableAtUtc.ToOffset(IndiaOffset).DateTime);
        return frame.Market.CompletedBars.Where(value =>
                value.Symbol == spotSymbol &&
                value.IntervalSeconds == settings.CandleIntervalSeconds &&
                DateOnly.FromDateTime(value.OpenTimeUtc.ToOffset(
                    IndiaOffset).DateTime) == date)
            .OrderBy(value => value.OpenTimeUtc)
            .Select(value => new StrategyPriceBar(value.OpenTimeUtc,
                value.Open, value.High, value.Low, value.Close)).ToArray();
    }

    private static ProfessionalExitPlan ExitPlan() => NiftySignalRecoveryPolicy.ExitPlan();

    private static IEnumerable<HistoricalReplayOpenPosition> NiftyPositions(
        HistoricalReplayPortfolioFrame frame) => frame.OpenPositions.Where(value =>
            value.Symbol.StartsWith("NIFTY", StringComparison.Ordinal) &&
            value.UnderlyingDirection is not null &&
            value.InitialStopPrice > 0m &&
            value.InitialStopPrice < value.EntryPrice);

    private bool TryOptionMark(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayOpenPosition position, out decimal mark)
    {
        mark = 0m;
        if (!frame.Market.LatestQuotes.TryGetValue(position.Symbol,
                out var quote) || quote.AvailableAtUtc < position.EntryAtUtc ||
            !quote.IsFreshAsOf(frame.Market.AvailableAtUtc, TimeSpan.FromSeconds(settings.MaximumOptionQuoteAgeSeconds)))
            return false;
        mark = quote.BidPrice ?? quote.LastPrice;
        return mark > 0m;
    }

    private bool TryUnderlyingMark(HistoricalReplayPortfolioFrame frame,
        out decimal price)
    {
        price = 0m;
        if (!frame.Market.LatestQuotes.TryGetValue(spotSymbol,
                out var quote) ||
            !quote.IsFreshAsOf(frame.Market.AvailableAtUtc, TimeSpan.FromSeconds(settings.MaximumUnderlyingQuoteAgeSeconds)))
            return false;
        price = quote.LastPrice;
        return price > 0m;
    }

    private static decimal Favourable(HistoricalReplayOpenPosition position,
        decimal mark) => Math.Max(position.EntryPrice,
            Math.Max(position.HighestDecisionPrice, mark));

    private static decimal Ema(decimal[] values, int period)
    {
        var multiplier = 2m / (period + 1m);
        var result = values[0];
        for (var index = 1; index < values.Length; index++)
            result = (values[index] - result) * multiplier + result;
        return result;
    }
}
