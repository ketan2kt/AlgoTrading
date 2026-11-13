using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record FailedSetupExitDecision(bool ShouldExit, decimal AdverseR,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Recognises an entry that never followed through and is now moving against
/// both the option and its underlying. This is distinct from a confirmed
/// session-wide reversal and never replaces the original protective stop.
/// </summary>
public static class FailedSetupExitPolicy
{
    // The reclaim test is a fallback for these Nifty paths. For regular Sensex
    // and Bank Nifty positions it is the only failed-setup confirmation.
    public static bool AllowsEntryReclaim(string market, string strategy)
    {
        if (RequiresConfirmedEntryFailure(market, strategy)) return true;
        if (market != "nifty") return false;
        var normalStrategy = strategy.StartsWith("exploration-", StringComparison.Ordinal)
            ? strategy["exploration-".Length..] : strategy;
        return normalStrategy is "opening-range-breakout" or "opening-range-retest" or
            "vwap-trend-pullback" or "ema-pullback-continuation" or
            "intraday-range-breakout-retest" or "vwap-rejection-reversal" or "momentum-expansion";
    }

    private static bool RequiresConfirmedEntryFailure(string market, string strategy) =>
        market is "sensex" or "bank-nifty" &&
        !strategy.StartsWith("Hero Zero|", StringComparison.OrdinalIgnoreCase) &&
        !strategy.StartsWith("Expansion Pair|", StringComparison.OrdinalIgnoreCase) &&
        !strategy.StartsWith("PaperOnly|Pyramid|", StringComparison.OrdinalIgnoreCase);

    public static FailedSetupExitDecision EvaluateForMarket(string market, string strategy,
        Direction direction, decimal entryOption, decimal optionPrice, decimal initialRisk,
        decimal peakOption, DateTimeOffset entryAt, DateTimeOffset observedAt,
        decimal underlyingPrice, DateTimeOffset underlyingAt,
        IReadOnlyList<StrategyPriceBar> bars, int intervalSeconds, int maximumAgeSeconds)
    {
        var adverseR = initialRisk <= 0m ? 0m : Math.Max(0m, (entryOption - optionPrice) / initialRisk);
        if (underlyingAt > observedAt || observedAt - underlyingAt > TimeSpan.FromSeconds(maximumAgeSeconds))
            return new(false, adverseR, ["Underlying quote is stale or future-dated."]);
        if (intervalSeconds <= 0 || bars.Count < 10 ||
            bars.Any(bar => bar.OpenTimeUtc.AddSeconds(intervalSeconds) > observedAt) ||
            observedAt - bars[^1].OpenTimeUtc.AddSeconds(intervalSeconds) > TimeSpan.FromSeconds(intervalSeconds))
            return new(false, adverseR, ["Completed underlying candle evidence is missing, incomplete or stale."]);
        var requireConfirmation = RequiresConfirmedEntryFailure(market, strategy);
        if (!requireConfirmation)
        {
            var original = Evaluate(direction, entryOption, optionPrice, initialRisk,
                peakOption, entryAt, observedAt, underlyingPrice, bars);
            if (original.ShouldExit || !AllowsEntryReclaim(market, strategy)) return original;
        }
        if (!IsAdverseCandidate(entryOption, optionPrice, initialRisk, peakOption, entryAt, observedAt) ||
            underlyingPrice <= 0m)
            return new(false, adverseR, ["Fast failed-setup prerequisites are not met."]);

        var signalIndex = -1;
        for (var index = 0; index < bars.Count; index++)
            if (bars[index].OpenTimeUtc.AddSeconds(intervalSeconds) <= entryAt) signalIndex = index;
        if (signalIndex < 0 || entryAt - bars[signalIndex].OpenTimeUtc.AddSeconds(intervalSeconds) >=
            TimeSpan.FromSeconds(intervalSeconds))
            return new(false, adverseR, ["Entry-candle reclaim: pre-entry signal candle is unavailable."]);
        for (var index = signalIndex + 1; index < bars.Count; index++)
            if (bars[index].OpenTimeUtc - bars[index - 1].OpenTimeUtc != TimeSpan.FromSeconds(intervalSeconds))
                return new(false, adverseR, ["Entry-candle reclaim: candle sequence has a gap."]);
        var signal = bars[signalIndex];
        var prior = bars[^2];
        var last = bars[^1];
        if (prior.OpenTimeUtc < entryAt)
            return new(false, adverseR, ["Entry-candle reclaim: two fully post-entry candles are required."]);
        var ema9 = bars[0].Close;
        foreach (var bar in bars.Skip(1)) ema9 = bar.Close * .2m + ema9 * .8m;
        var recent = bars.TakeLast(10).ToArray();
        var atr = recent.Skip(1).Select((bar, index) => Math.Max(bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - recent[index].Close), Math.Abs(bar.Low - recent[index].Close)))).Average();
        var buffer = atr * .10m;
        var reclaimed = atr > 0m && (direction == Direction.Sell
            ? (requireConfirmation || signal.Close < signal.Open) &&
              prior.Close > prior.Open && last.Close > last.Open &&
              last.Close > prior.Close && prior.Close > signal.High + buffer && last.Close > signal.High + buffer &&
              underlyingPrice > signal.High + buffer && underlyingPrice > ema9 + buffer
            : (requireConfirmation || signal.Close > signal.Open) &&
              prior.Close < prior.Open && last.Close < last.Open &&
              last.Close < prior.Close && prior.Close < signal.Low - buffer && last.Close < signal.Low - buffer &&
              underlyingPrice < signal.Low - buffer && underlyingPrice < ema9 - buffer);
        return reclaimed
            ? new(true, adverseR, [$"Entry-candle reclaim: option lost {adverseR:F2}R without +0.50R; two completed opposing candles invalidated the signal candle and the fresh quote still holds beyond that boundary and EMA9."])
            : new(false, adverseR, ["Entry-candle reclaim is not confirmed by two opposing closes and the fresh quote; retain existing stop."]);
    }

    public static bool IsAdverseCandidate(decimal entryOption, decimal optionPrice,
        decimal initialRisk, decimal peakOption, DateTimeOffset entryAt,
        DateTimeOffset observedAt) =>
        entryOption > 0m && initialRisk > 0m && optionPrice > 0m &&
        observedAt - entryAt >= TimeSpan.FromMinutes(1) &&
        // The old eight-minute cutoff missed later-confirmed failed openings.
        // Keep the window bounded and require fresh underlying confirmation below.
        observedAt - entryAt <= TimeSpan.FromMinutes(15) &&
        Math.Max(0m, peakOption - entryOption) < initialRisk * .50m &&
        entryOption - optionPrice >= initialRisk * .35m;

    public static FailedSetupExitDecision Evaluate(Direction underlyingDirection,
        decimal entryOption, decimal optionPrice, decimal initialRisk,
        decimal peakOption, DateTimeOffset entryAt, DateTimeOffset observedAt,
        decimal underlyingPrice, IReadOnlyList<StrategyPriceBar> completedBars)
    {
        var adverseR = initialRisk <= 0m ? 0m :
            Math.Max(0m, (entryOption - optionPrice) / initialRisk);
        if (!IsAdverseCandidate(entryOption, optionPrice, initialRisk, peakOption,
                entryAt, observedAt) || underlyingPrice <= 0m || completedBars.Count < 10)
            return new(false, adverseR, ["Fast failed-setup prerequisites are not met."]);

        var last = completedBars[^1];
        var prior = completedBars[^2];
        if (last.OpenTimeUtc < entryAt)
            return new(false, adverseR, ["No completed underlying candle after entry."]);

        var closes = completedBars.Select(value => value.Close).ToArray();
        var ema9 = closes[0];
        foreach (var close in closes.Skip(1))
            ema9 = close * .2m + ema9 * .8m;
        var recent = completedBars.TakeLast(10).ToArray();
        var atr = recent.Skip(1).Select((bar, index) => Math.Max(bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - recent[index].Close),
                Math.Abs(bar.Low - recent[index].Close)))).Average();
        if (atr <= 0m)
            return new(false, adverseR, ["Underlying volatility is unavailable."]);

        var failedResumption = underlyingDirection == Direction.Buy
            ? last.Close < last.Open && last.Close < prior.Close &&
              underlyingPrice < last.Close - atr * .10m &&
              underlyingPrice < ema9 - atr * .10m
            : last.Close > last.Open && last.Close > prior.Close &&
              underlyingPrice > last.Close + atr * .10m &&
              underlyingPrice > ema9 + atr * .10m;
        return failedResumption
            ? new(true, adverseR,
                [$"Option lost {adverseR:F2}R without reaching +0.50R; the latest completed " +
                 "underlying candle opposed the entry and a fresh quote broke beyond its close and EMA9."])
            : new(false, adverseR,
                ["Option is adverse, but the underlying resumption has not failed."]);
    }
}
