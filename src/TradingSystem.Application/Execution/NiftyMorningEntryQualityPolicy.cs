using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record NiftyMorningEntryQualityDecision(bool Permitted,
    string RejectionOutcome, IReadOnlyList<string> Reasons);

// Entry selection only. Calibrated against the one-minute Nifty engine; chart
// timeframe does not affect it. Call explicitly from Nifty, never other markets.
public static class NiftyMorningEntryQualityPolicy
{
    public const string Version = "nifty-morning-quality-v1";
    private static readonly NiftyMorningEntryQualityDecision Allowed = new(true, "", []);

    public static NiftyMorningEntryQualityDecision Evaluate(TimeOnly indiaTime,
        IReadOnlyList<StrategyPriceBar> completedSession, Direction direction,
        int intervalSeconds = 60)
    {
        ArgumentNullException.ThrowIfNull(completedSession);
        if (indiaTime < new TimeOnly(9, 30) || indiaTime >= new TimeOnly(11, 0) ||
            intervalSeconds != 60 || completedSession.Count < 15)
            return Allowed; // Existing warmup/freshness/risk gates remain mandatory.

        var bars = completedSession.OrderBy(b => b.OpenTimeUtc).ToArray();
        var recent = bars.TakeLast(15).ToArray();
        if (recent.Zip(recent.Skip(1)).Any(p =>
                p.Second.OpenTimeUtc - p.First.OpenTimeUtc != TimeSpan.FromMinutes(1)) ||
            recent[0].OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).Date !=
                recent[^1].OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).Date)
            return Allowed; // Do not interpret absent candles as price rotation.

        var context = SharedMarketContextPolicy.Analyze(bars, intervalSeconds);
        if (context.DirectionFlipRate < .40m) return Allowed;

        var atr = recent.Skip(1).Select((b, i) => Math.Max(b.High - b.Low,
            Math.Max(Math.Abs(b.High - recent[i].Close), Math.Abs(b.Low - recent[i].Close)))).Average();
        var body = Math.Abs(bars[^1].Close - bars[^1].Open);
        if (context.Efficiency < .38m && atr > 0m && body > atr)
            return new(false, "NiftyMorningImpulseRejected",
                [FormattableString.Invariant(
                    $"Morning rotation: efficiency {context.Efficiency:P0}, direction flips {context.DirectionFlipRate:P0}, candle body {body / atr:F2} ATR. Wait for a smaller confirmed resumption instead of chasing this impulse.")]);

        var confirmation = MiddayChopConfirmationPolicy.ConfirmCompletedStructure(
            bars, direction, intervalSeconds);
        return confirmation.Permitted ? Allowed :
            new(false, "NiftyMorningStructureRejected",
                ["Morning direction is frequently flipping; require a completed five-minute structure break or pullback resumption.", .. confirmation.Reasons]);
    }
}
