using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record UnderlyingTrendInvalidationResult(
    bool ShouldExit,
    decimal EvidenceScore,
    IReadOnlyList<string> SupportingEvidence);

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

        var recent = completedBars.TakeLast(12).ToArray();
        var latest = recent[^1];
        var previous = recent[^2];
        var structure = MarketStructureAnalyzer.Analyze(recent);
        var priorSwingBars = recent.SkipLast(2).TakeLast(4).ToArray();
        var evidence = new List<string>();

        if (originalUnderlyingDirection == Direction.Buy)
        {
            if (latest.Close < fastEma && previous.Close < fastEma)
                evidence.Add("Two completed Nifty candles closed below EMA 9.");
            if (fastEma < slowEma)
                evidence.Add("EMA 9 crossed below EMA 21.");
            if (structure.Direction == MarketStructureDirection.Bearish &&
                structure.Strength >= minimumStructureStrength)
                evidence.Add($"Nifty structure changed to bearish ({structure.Strength:P0} strength).");
            if (latest.Close < priorSwingBars.Min(x => x.Low))
                evidence.Add("Nifty closed below the recent confirmed swing low.");
        }
        else
        {
            if (latest.Close > fastEma && previous.Close > fastEma)
                evidence.Add("Two completed Nifty candles closed above EMA 9.");
            if (fastEma > slowEma)
                evidence.Add("EMA 9 crossed above EMA 21.");
            if (structure.Direction == MarketStructureDirection.Bullish &&
                structure.Strength >= minimumStructureStrength)
                evidence.Add($"Nifty structure changed to bullish ({structure.Strength:P0} strength).");
            if (latest.Close > priorSwingBars.Max(x => x.High))
                evidence.Add("Nifty closed above the recent confirmed swing high.");
        }

        return new(evidence.Count >= requiredEvidenceCount, evidence.Count / 4m, evidence);
    }
}
