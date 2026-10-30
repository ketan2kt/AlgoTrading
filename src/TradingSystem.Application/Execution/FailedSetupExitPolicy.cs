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
