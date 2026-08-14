using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record UnderlyingTrendInvalidationResult(
    bool ShouldExit,
    decimal EvidenceScore,
    IReadOnlyList<string> SupportingEvidence,
    SessionBehaviour Behaviour = SessionBehaviour.Unknown);

public enum SessionBehaviour
{
    Unknown = 0,
    Trending = 1,
    RangeBound = 2,
    ZigZag = 3
}

public static class UnderlyingTrendInvalidationPolicy
{
    public static UnderlyingTrendInvalidationResult Evaluate(
        Direction originalUnderlyingDirection,
        IReadOnlyList<StrategyPriceBar> completedBars,
        decimal fastEma,
        decimal slowEma,
        decimal minimumStructureStrength = 0.55m,
        int requiredEvidenceCount = 3)
    {
        ArgumentNullException.ThrowIfNull(completedBars);
        if (originalUnderlyingDirection is not (Direction.Buy or Direction.Sell))
            throw new ArgumentOutOfRangeException(nameof(originalUnderlyingDirection));
        if (minimumStructureStrength is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumStructureStrength));
        if (requiredEvidenceCount is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(requiredEvidenceCount));
        if (completedBars.Count < 8 || fastEma <= 0 || slowEma <= 0)
            return new(false, 0m, ["Insufficient completed Nifty candles for reversal confirmation."]);

        var session = completedBars.ToArray();
        var recent = session.TakeLast(12).ToArray();
        var latest = recent[^1];
        var structure = MarketStructureAnalyzer.Analyze(recent);
        var priorSwingBars = recent.SkipLast(2).TakeLast(4).ToArray();
        var evidence = new List<string>();
        var behaviour = ClassifySession(session, fastEma, slowEma);
        var persistent = false;
        var swingBroken = false;
        var oppositeStructure = false;

        if (originalUnderlyingDirection == Direction.Buy)
        {
            persistent = recent.TakeLast(3).All(bar => bar.Close < fastEma);
            if (persistent) evidence.Add("Three completed Nifty candles remained below EMA 9.");
            if (fastEma < slowEma)
                evidence.Add("EMA 9 crossed below EMA 21.");
            oppositeStructure = structure.Direction == MarketStructureDirection.Bearish &&
                                structure.Strength >= minimumStructureStrength;
            if (oppositeStructure &&
                structure.Strength >= minimumStructureStrength)
                evidence.Add($"Nifty structure changed to bearish ({structure.Strength:P0} strength).");
            swingBroken = latest.Close < priorSwingBars.Min(x => x.Low);
            if (swingBroken)
                evidence.Add("Nifty closed below the recent confirmed swing low.");
        }
        else
        {
            persistent = recent.TakeLast(3).All(bar => bar.Close > fastEma);
            if (persistent) evidence.Add("Three completed Nifty candles remained above EMA 9.");
            if (fastEma > slowEma)
                evidence.Add("EMA 9 crossed above EMA 21.");
            oppositeStructure = structure.Direction == MarketStructureDirection.Bullish &&
                                structure.Strength >= minimumStructureStrength;
            if (oppositeStructure &&
                structure.Strength >= minimumStructureStrength)
                evidence.Add($"Nifty structure changed to bullish ({structure.Strength:P0} strength).");
            swingBroken = latest.Close > priorSwingBars.Max(x => x.High);
            if (swingBroken)
                evidence.Add("Nifty closed above the recent confirmed swing high.");
        }

        var evidenceRequired = behaviour is SessionBehaviour.ZigZag or SessionBehaviour.RangeBound
            ? 4
            : requiredEvidenceCount;
        var confirmed = evidence.Count >= evidenceRequired && persistent && swingBroken &&
                        (behaviour == SessionBehaviour.Trending || oppositeStructure);
        evidence.Add($"Session behaviour: {behaviour}.");
        return new(confirmed, Math.Min(1m, (evidence.Count - 1) / 4m), evidence, behaviour);
    }

    private static SessionBehaviour ClassifySession(StrategyPriceBar[] bars,
        decimal fastEma, decimal slowEma)
    {
        if (bars.Length < 8) return SessionBehaviour.Unknown;
        var changes = bars.Zip(bars.Skip(1))
            .Select(pair => Math.Sign(pair.Second.Close - pair.First.Close))
            .Where(sign => sign != 0).ToArray();
        var alternations = changes.Zip(changes.Skip(1)).Count(pair => pair.First != pair.Second);
        var alternationRatio = changes.Length > 1
            ? (decimal)alternations / (changes.Length - 1)
            : 0m;
        var range = bars.Max(bar => bar.High) - bars.Min(bar => bar.Low);
        var emaSeparation = Math.Abs(fastEma - slowEma);
        if (alternationRatio >= 0.55m) return SessionBehaviour.ZigZag;
        if (range > 0 && emaSeparation / range <= 0.08m) return SessionBehaviour.RangeBound;
        return SessionBehaviour.Trending;
    }
}
