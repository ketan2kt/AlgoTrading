using System.Text.Json;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalMarketOptionExitSettings(TimeOnly ForcedExit,
    bool PartialEnabled, bool TargetExtensionEnabled, bool InvalidationEnabled,
    decimal MinimumReversalStrength, int RequiredReversalEvidence,
    bool ProtectProfitableReversals, decimal ReversalLockFraction,
    int MaximumOptionAgeSeconds, int MaximumUnderlyingAgeSeconds, int CandleIntervalSeconds);

// Offline wrapper around the same normal-session option position policies used
// by MultiMarketPaperTradingService. LTP drives paper decisions; the runner
// prices a later modeled sale from bid, never treating LTP as an executable bid.
public sealed class HistoricalMarketOptionPositionPolicy(
    IHistoricalReplayPortfolioPolicy entryPolicy, string market, string underlyingSymbol,
    HistoricalMarketOptionExitSettings settings,
    IReadOnlyList<NiftyOptionContractCandidate> contracts) : IHistoricalReplayPortfolioPolicy
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
    public string CodeVersion => entryPolicy.CodeVersion + "/market-option-exit-v1";
    public bool ProductionPolicyParityVerified => entryPolicy.ProductionPolicyParityVerified && settings.CandleIntervalSeconds is 60 or 300;
    public string ConfigurationManifest => entryPolicy.ConfigurationManifest + "|marketExit=" +
        JsonSerializer.Serialize(new { market, underlyingSymbol, settings,
            Contracts = contracts.OrderBy(value => value.TradingSymbol, StringComparer.Ordinal)
                .Select(value => new { value.TradingSymbol, value.TickSize, value.LotSize }) });

    public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) =>
        entryPolicy.Evaluate(frame);

    public HistoricalReplayCandidate? AssessDeferredEntry(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayCandidate pending, HistoricalReplayQuote optionQuote) =>
        entryPolicy.AssessDeferredEntry(frame, pending, optionQuote);

    public IReadOnlyList<HistoricalReplayStopUpdate> EvaluateStopUpdates(HistoricalReplayPortfolioFrame frame)
    {
        var updates = new List<HistoricalReplayStopUpdate>();
        foreach (var position in Positions(frame))
        {
            if (!TryMark(frame, position, out var price)) continue;
            var plan = Plan(position);
            var stop = MarketOptionPositionPolicy.ProtectedStop(Direction.Buy,
                position.EntryPrice, Risk(position), Favourable(position, price),
                position.StopPrice, position.Quantity, plan);
            var reversal = Reversal(frame, position, price, stop);
            if (reversal.Stop > position.StopPrice)
                updates.Add(new(position.Symbol, frame.Market.AvailableAtUtc, reversal.Stop));
        }
        return updates;
    }

    public bool ShouldExtendTarget(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayOpenPosition position)
    {
        if (!Positions(frame).Any(value => value.Symbol == position.Symbol) ||
            !TryMark(frame, position, out var price)) return false;
        var reversal = Reversal(frame, position, price, position.StopPrice);
        return price > position.StopPrice && !reversal.ShouldExit && !reversal.ProtectProfit &&
            settings.TargetExtensionEnabled && Plan(position).ExtendTarget &&
            MarketOptionPositionPolicy.Continuation(Bars(frame, false), position.UnderlyingDirection!.Value);
    }

    public IReadOnlyList<HistoricalReplayExitRequest> EvaluateExits(HistoricalReplayPortfolioFrame frame) =>
        Positions(frame).Where(value => TryMark(frame, value, out _))
            .Select(value => (Position: value, Decision: Decision(frame, value)))
            .Where(value => value.Decision.ExitReason is not null)
            .Select(value => new HistoricalReplayExitRequest(value.Position.Symbol,
                frame.Market.AvailableAtUtc, value.Decision.ExitReason!)).ToArray();

    public IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(HistoricalReplayPortfolioFrame frame) =>
        Positions(frame).Where(value => TryMark(frame, value, out _))
            .Select(value => (Position: value, Decision: Decision(frame, value)))
            .Where(value => value.Decision.PartialQuantity > 0)
            .Select(value => new HistoricalReplayPartialExit(value.Position.Symbol,
                frame.Market.AvailableAtUtc, value.Decision.PartialQuantity, "PartialProfit")).ToArray();

    private MarketOptionPositionDecision Decision(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayOpenPosition position)
    {
        if (!TryMark(frame, position, out var price)) return new(null, 0);
        var reversal = Reversal(frame, position, price, position.StopPrice);
        var failed = false;
        if (TryUnderlying(frame, out var underlying) &&
            FailedSetupExitPolicy.IsAdverseCandidate(position.EntryPrice, price, Risk(position),
                Favourable(position, price), position.EntryAtUtc, frame.Market.AvailableAtUtc))
            failed = FailedSetupExitPolicy.Evaluate(position.UnderlyingDirection!.Value,
                position.EntryPrice, price, Risk(position), Favourable(position, price),
                position.EntryAtUtc, frame.Market.AvailableAtUtc, underlying,
                Bars(frame, true).TakeLast(30).ToArray()).ShouldExit;
        var forced = TimeOnly.FromDateTime(frame.Market.AvailableAtUtc.ToOffset(IndiaOffset).DateTime)
            >= settings.ForcedExit;
        return MarketOptionPositionPolicy.Decide(Direction.Buy, price, position.EntryPrice,
            position.StopPrice, position.TargetPrice, Risk(position), position.Quantity,
            position.LotSize!.Value, Plan(position), false, reversal.ShouldExit, failed,
            ShouldExtendTarget(frame, position), forced, settings.PartialEnabled,
            position.Quantity != position.OriginalQuantity);
    }

    private (bool ShouldExit, bool ProtectProfit, decimal Stop) Reversal(
        HistoricalReplayPortfolioFrame frame, HistoricalReplayOpenPosition position,
        decimal price, decimal stop)
    {
        var invalidation = settings.InvalidationEnabled && TryUnderlying(frame, out _)
            ? MarketOptionPositionPolicy.Invalidation(Bars(frame, true),
                position.UnderlyingDirection!.Value, position.EntryAtUtc, frame.Market.AvailableAtUtc,
                settings.CandleIntervalSeconds, settings.MaximumUnderlyingAgeSeconds,
                settings.MinimumReversalStrength, settings.RequiredReversalEvidence)
            : new UnderlyingTrendInvalidationResult(false, 0m, []);
        var contract = contracts.Single(value => value.TradingSymbol == position.Symbol);
        return MarketOptionPositionPolicy.Reversal(market, invalidation, Direction.Buy,
            position.EntryPrice, price, stop, settings.ProtectProfitableReversals,
            settings.ReversalLockFraction, contract.TickSize);
    }

    private IEnumerable<HistoricalReplayOpenPosition> Positions(HistoricalReplayPortfolioFrame frame) =>
        frame.OpenPositions.Where(value => value.Symbol.StartsWith(underlyingSymbol, StringComparison.Ordinal) &&
            value.UnderlyingDirection is not null && value.Quantity > 0 && value.LotSize is > 0 &&
            value.InitialTargetPrice > value.EntryPrice &&
            !value.Strategy.StartsWith("Hero Zero|", StringComparison.Ordinal) &&
            !value.Strategy.StartsWith("Expansion Pair|", StringComparison.Ordinal) &&
            contracts.Any(contract => contract.TradingSymbol == value.Symbol && contract.TickSize > 0m));

    private StrategyPriceBar[] Bars(HistoricalReplayPortfolioFrame frame, bool currentSession)
    {
        var date = DateOnly.FromDateTime(frame.Market.AvailableAtUtc.ToOffset(IndiaOffset).DateTime);
        return frame.Market.CompletedBars.Where(value => value.Symbol == underlyingSymbol &&
                value.IntervalSeconds == settings.CandleIntervalSeconds &&
                value.AvailableAtUtc <= frame.Market.AvailableAtUtc &&
                value.OpenTimeUtc.AddSeconds(value.IntervalSeconds) <= frame.Market.AvailableAtUtc &&
                (!currentSession || DateOnly.FromDateTime(value.OpenTimeUtc.ToOffset(IndiaOffset).DateTime) == date))
            .OrderBy(value => value.OpenTimeUtc).Select(value => new StrategyPriceBar(value.OpenTimeUtc,
                value.Open, value.High, value.Low, value.Close)).ToArray();
    }

    private bool TryMark(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayOpenPosition position, out decimal price)
    {
        price = 0m;
        if (!frame.Market.LatestQuotes.TryGetValue(position.Symbol, out var quote) ||
            quote.AvailableAtUtc < position.EntryAtUtc || quote.AvailableAtUtc > frame.Market.AvailableAtUtc ||
            !quote.IsFreshAsOf(frame.Market.AvailableAtUtc, TimeSpan.FromSeconds(settings.MaximumOptionAgeSeconds)))
            return false;
        price = quote.LastPrice;
        return price > 0m;
    }

    private bool TryUnderlying(HistoricalReplayPortfolioFrame frame, out decimal price)
    {
        price = 0m;
        if (!frame.Market.LatestQuotes.TryGetValue(underlyingSymbol, out var quote) ||
            quote.AvailableAtUtc > frame.Market.AvailableAtUtc ||
            !quote.IsFreshAsOf(frame.Market.AvailableAtUtc, TimeSpan.FromSeconds(settings.MaximumUnderlyingAgeSeconds)))
            return false;
        price = quote.LastPrice;
        return price > 0m;
    }

    private static decimal Risk(HistoricalReplayOpenPosition position) =>
        Math.Abs(position.InitialTargetPrice - position.EntryPrice);
    private static decimal Favourable(HistoricalReplayOpenPosition position, decimal price) =>
        Math.Max(position.EntryPrice, Math.Max(position.HighestDecisionPrice, price));
    private static ProfessionalExitPlan Plan(HistoricalReplayOpenPosition position) =>
        ProfessionalExitPolicy.For(MarketOptionPositionPolicy.Regime(position.Strategy));
}
