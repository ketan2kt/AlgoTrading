using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record ExpansionPairEntryDecision(
    bool Permitted,
    decimal Confidence,
    decimal BaselineRange,
    decimal CompressionRatio,
    decimal ReleaseRatio,
    IReadOnlyList<string> Reasons);

public static class IntradayExpansionPairPolicy
{
    public static StrategyPriceBar[] CompletedFiveMinuteBars(
        IReadOnlyList<StrategyPriceBar> source, int intervalSeconds, DateTimeOffset now)
    {
        if (intervalSeconds <= 0 || intervalSeconds > 300 || 300 % intervalSeconds != 0)
            return [];
        return source.GroupBy(bar => bar.OpenTimeUtc.ToUnixTimeSeconds() / 300 * 300)
            .OrderBy(group => group.Key)
            .Where(group => group.Key + 300 <= now.ToUnixTimeSeconds())
            .Select(group => group.OrderBy(bar => bar.OpenTimeUtc).ToArray())
            .Where(bars => bars.Length == 300 / intervalSeconds &&
                bars.Select(bar => bar.OpenTimeUtc.ToUnixTimeSeconds()).SequenceEqual(
                    Enumerable.Range(0, bars.Length).Select(i =>
                        bars[0].OpenTimeUtc.ToUnixTimeSeconds() / 300 * 300 + i * intervalSeconds)))
            .Select(bars => new StrategyPriceBar(bars[0].OpenTimeUtc, bars[0].Open,
                bars.Max(bar => bar.High), bars.Min(bar => bar.Low), bars[^1].Close)).ToArray();
    }

    public static ExpansionPairEntryDecision Evaluate(IReadOnlyList<StrategyPriceBar> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 11)
            return Reject("At least eleven completed five-minute candles are required.");

        var bars = source.TakeLast(11).ToArray();
        var baseline = bars.Take(6).ToArray();
        var squeeze = bars.Skip(6).Take(4).ToArray();
        var release = bars[^1];
        var baselineRange = baseline.Average(Range);
        var squeezeRange = squeeze.Average(Range);
        if (baselineRange <= 0m)
            return Reject("The baseline range is invalid.");

        var compressionRatio = squeezeRange / baselineRange;
        if (compressionRatio > .78m)
            return Reject($"Recent range is {compressionRatio:P0} of baseline; a 78% or tighter squeeze is required.", baselineRange, compressionRatio);

        var squeezeHigh = squeeze.Max(value => value.High);
        var squeezeLow = squeeze.Min(value => value.Low);
        var squeezeBand = squeezeHigh - squeezeLow;
        if (squeezeBand > baselineRange * 1.65m)
            return Reject("The four-candle structure is too wide to qualify as compression.", baselineRange, compressionRatio);

        var releaseRange = Range(release);
        var releaseRatio = squeezeRange <= 0m ? 0m : releaseRange / squeezeRange;
        var bodyRatio = releaseRange <= 0m ? 0m : Math.Abs(release.Close - release.Open) / releaseRange;
        var released = release.Close > squeezeHigh || release.Close < squeezeLow;
        if (!released || releaseRatio < 1.15m || bodyRatio < .45m)
            return Reject("Compression exists, but no decisive 1.15x candle has closed outside the squeeze.", baselineRange, compressionRatio, releaseRatio);

        // A paired debit is useful while magnitude is emerging and direction is not yet mature.
        // It is not opened after an already exhausted multi-candle impulse.
        var baselineNet = Math.Abs(baseline[^1].Close - baseline[0].Open);
        var baselineTravel = baseline.Sum(value => Math.Abs(value.Close - value.Open));
        var directionalEfficiency = baselineTravel <= 0m ? 0m : baselineNet / baselineTravel;
        if (directionalEfficiency > .72m)
            return Reject("The preceding move is already strongly directional; a paired debit would chase mature premium.", baselineRange, compressionRatio, releaseRatio);

        var confidence = Math.Clamp(.55m + (.78m - compressionRatio) * .55m +
                                    Math.Min(.15m, (releaseRatio - 1.15m) * .18m), .55m, .90m);
        return new(true, confidence, baselineRange, compressionRatio, releaseRatio,
        [
            $"Four-candle range compressed to {compressionRatio:P0} of the six-candle baseline.",
            $"A {releaseRatio:F2}x release candle broke the squeeze with {bodyRatio:P0} body quality.",
            $"Pre-squeeze directional efficiency was {directionalEfficiency:P0}; magnitude is emerging before direction is mature."
        ]);
    }

    private static decimal Range(StrategyPriceBar value) => value.High - value.Low;

    private static ExpansionPairEntryDecision Reject(string reason, decimal baseline = 0m,
        decimal compression = 0m, decimal release = 0m) =>
        new(false, 0m, baseline, compression, release, [reason]);
}

public sealed record ExpansionPairCandidateDecision(
    bool Permitted,
    int Lots,
    decimal CombinedPremium,
    decimal EstimatedRisk,
    IReadOnlyList<string> Reasons);

public static class ExpansionPairCandidatePolicy
{
    public static ExpansionPairCandidateDecision Evaluate(HeroZeroCandidate call,
        HeroZeroCandidate put, decimal maximumCombinedPremium, decimal maximumPremiumImbalance,
        decimal maximumBasketRisk, decimal stopPercent, int maximumLots)
    {
        if (call.OptionType != "CE" || put.OptionType != "PE" ||
            call.Expiry != put.Expiry || call.LotSize != put.LotSize)
            return Reject("The two legs must be same-expiry CE/PE contracts with the same lot size.");
        var combined = call.Premium + put.Premium;
        if (combined <= 0m || combined > maximumCombinedPremium)
            return Reject($"Combined debit {combined:F2} exceeds the configured {maximumCombinedPremium:F2} ceiling.", combined);
        var premiumRatio = Math.Max(call.Premium, put.Premium) / Math.Min(call.Premium, put.Premium);
        if (premiumRatio > maximumPremiumImbalance)
            return Reject($"Leg premium ratio {premiumRatio:F2} exceeds {maximumPremiumImbalance:F2}; sensitivity is not balanced.", combined);
        if (call.Score < .55m || put.Score < .55m)
            return Reject("Both legs must pass the liquidity, volume and OI quality floor.", combined);

        var riskPerLot = combined * call.LotSize * stopPercent;
        var lots = riskPerLot <= 0m ? 0 : Math.Min(maximumLots,
            (int)Math.Floor(maximumBasketRisk / riskPerLot));
        if (lots < 1)
            return Reject($"One paired lot would risk {riskPerLot:F2}, above the basket budget.", combined);
        return new(true, lots, combined, riskPerLot * lots,
        [
            $"Premium ratio {premiumRatio:F2} provides a practical delta/sensitivity proxy where broker Greeks are unavailable.",
            $"{lots} lot(s) risk approximately {riskPerLot * lots:F2} at the basket stop.",
            $"CE/PE OI quality scores are {call.Score:F2}/{put.Score:F2}."
        ]);
    }

    private static ExpansionPairCandidateDecision Reject(string reason, decimal combined = 0m) =>
        new(false, 0, combined, 0m, [reason]);
}

public enum ExpansionPairExitAction
{
    Hold,
    StopBasket,
    TimeExit,
    ForceExit,
    RecoverAndRun,
    TrailRunner,
    ProtectBasketProfit
}

public static class ExpansionPairExitPolicy
{
    public const decimal LegFloorStop = 1m;

    public static bool ShouldStopLeg(MarketPaperPosition leg) =>
        leg.Status == "Active" && leg.CurrentPrice <= leg.StopLoss;

    public static decimal MarkedBasketValue(IEnumerable<MarketPaperPosition> legs) =>
        legs.Sum(leg => leg.CurrentPrice * leg.RemainingQuantity +
            leg.EntryPrice * (leg.Quantity - leg.RemainingQuantity) +
            leg.RealisedPnl + leg.CostsPaid);

    public static ExpansionPairExitAction Evaluate(decimal entryValue, decimal currentValue,
        decimal peakValue, TimeSpan age, TimeOnly localTime, bool recoveryTaken,
        decimal stopPercent, decimal minimumExpansionPercent, decimal runnerTrailFraction,
        TimeOnly forcedExit)
    {
        if (localTime >= forcedExit) return ExpansionPairExitAction.ForceExit;
        if (entryValue <= 0m) return ExpansionPairExitAction.StopBasket;
        if (!recoveryTaken && currentValue <= entryValue * (1m - stopPercent))
            return ExpansionPairExitAction.StopBasket;
        // Protect an actual basket expansion even if lot granularity prevents partial recovery.
        if (!recoveryTaken && peakValue >= entryValue * (1m + minimumExpansionPercent) &&
            currentValue <= entryValue + (peakValue - entryValue) * .50m)
            return ExpansionPairExitAction.ProtectBasketProfit;
        if (!recoveryTaken && age >= TimeSpan.FromMinutes(15) &&
            peakValue < entryValue * (1m + minimumExpansionPercent))
            return ExpansionPairExitAction.TimeExit;
        if (!recoveryTaken && currentValue >= entryValue * 1.25m)
            return ExpansionPairExitAction.RecoverAndRun;
        if (recoveryTaken && peakValue > 0m &&
            currentValue <= peakValue * (1m - runnerTrailFraction))
            return ExpansionPairExitAction.TrailRunner;
        return ExpansionPairExitAction.Hold;
    }
}
