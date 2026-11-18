using System.Globalization;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record SensexStalledTrendDecision(bool Permitted, string? Rejection,
    IReadOnlyList<string> Reasons);

// Sensex persistent-thesis admission only. A directional thesis can stay
// valid through a pause, but that alone does not justify a new entry.
public static class SensexStalledTrendEntryPolicy
{
    public const string Version = "sensex-stalled-thesis-v2";
    public static readonly TimeOnly CashSessionOpen = new(9, 15);
    public const string Stalled = "SensexStalledThesisDeferred";
    public const string Incomplete = "SensexStalledThesisDataIncomplete";
    private static readonly SensexStalledTrendDecision Allowed = new(true, null, []);

    public static SensexStalledTrendDecision Evaluate(bool persistentDirectionalEntry,
        IReadOnlyList<StrategyPriceBar> bars, Direction direction, DateTimeOffset sessionOpen,
        DateTimeOffset asOf, int intervalSeconds)
    {
        if (!persistentDirectionalEntry) return Allowed;
        ArgumentNullException.ThrowIfNull(bars);
        if (direction is not (Direction.Buy or Direction.Sell))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (intervalSeconds is not (60 or 300))
            return RejectIncomplete("The stalled-thesis check requires one- or five-minute cash candles.");
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        // At 10:15 there is no completed 09:15 close exactly 60 minutes
        // earlier. The first 60-minute close-to-close comparison is at 10:16
        // with minute bars, or 10:20 with five-minute bars.
        if (asOf < sessionOpen.AddHours(1).Add(interval)) return Allowed;

        var hourSteps = 3600 / intervalSeconds;
        var fiveMinuteSteps = 300 / intervalSeconds;
        var history = bars.Where(b => b.OpenTimeUtc >= sessionOpen &&
                b.OpenTimeUtc.Add(interval) <= asOf)
            .TakeLast(Math.Max(15, hourSteps + 1)).ToArray();
        var tail = history.TakeLast(hourSteps + 1).ToArray();
        if (tail.Length != hourSteps + 1 ||
            asOf - tail[^1].OpenTimeUtc.Add(interval) >= interval ||
            tail.Zip(tail.Skip(1)).Any(pair => pair.Second.OpenTimeUtc - pair.First.OpenTimeUtc != interval))
            return RejectIncomplete("The latest hour of completed Sensex cash candles is not contiguous and current.");

        var recent = history.TakeLast(15).ToArray();
        var atr = recent.Skip(1).Select((bar, index) =>
            Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - recent[index].Close),
                Math.Abs(bar.Low - recent[index].Close)))).Average();
        if (atr <= 0m) return RejectIncomplete("Sensex cash ATR is unavailable for the stalled-thesis check.");

        var close = tail[^1].Close;
        var sign = direction == Direction.Buy ? 1m : -1m;
        var progress5 = sign * (close - tail[^(fiveMinuteSteps + 1)].Close);
        var progress60 = sign * (close - tail[0].Close);
        var priorSwingBars = tail.SkipLast(2).TakeLast(3);
        var swing = direction == Direction.Buy ? priorSwingBars.Max(b => b.High) : priorSwingBars.Min(b => b.Low);
        // An hour-old directional move sustains the thesis, not a fresh entry.
        // Require recent progress or a newly cleared local swing to resume it.
        var acceptedSwing = tail.TakeLast(2).All(bar => sign * (bar.Close - swing) > 0m);
        if (progress5 >= atr || acceptedSwing)
            return Allowed;

        return new(false, Stalled,
            [string.Create(CultureInfo.InvariantCulture,
                $"Sensex thesis stalled: 5m progress {progress5:F2} is below {atr:F2} ATR; 60m progress {progress60:F2} is context only. Wait for 5m progress of one ATR or two completed closes {(direction == Direction.Buy ? "above" : "below")} the prior three-bar swing at {swing:F2}.")]);
    }

    private static SensexStalledTrendDecision RejectIncomplete(string reason) =>
        new(false, Incomplete, [reason]);
}
