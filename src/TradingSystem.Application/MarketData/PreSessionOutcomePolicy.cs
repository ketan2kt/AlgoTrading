namespace TradingSystem.Application.MarketData;

public static class PreSessionOutcomePolicy
{
    public static PreSessionObservedOutcome Assess(
        string market, DateOnly sessionDate,
        decimal open, decimal high, decimal low, decimal close,
        int recordedCandles, DateTimeOffset firstCandleAtUtc,
        DateTimeOffset lastCandleAtUtc, DateTimeOffset assessedAtUtc,
        PreSessionMarketBrief? morning)
    {
        var localStart = firstCandleAtUtc.ToOffset(TimeSpan.FromHours(5.5));
        var localEnd = lastCandleAtUtc.ToOffset(TimeSpan.FromHours(5.5));
        var full = recordedCandles >= 250 &&
                   TimeOnly.FromDateTime(localStart.DateTime) <= new TimeOnly(9, 20) &&
                   TimeOnly.FromDateTime(localEnd.DateTime) >= new TimeOnly(15, 20);
        var coverage = full ? "Sufficient" : "Partial";
        var span = high - low;
        var movement = close - open;
        var direction = span <= 0 ? "Mixed" : movement >= span * .4m ? "Up" :
            movement <= -span * .4m ? "Down" : "Mixed";
        var judgement = morning?.Judgement ?? "Unavailable";
        var assessment = !full || judgement is not ("Up" or "Down") ? "Unscored" :
            judgement == direction ? "Correct" : "Incorrect";
        return new PreSessionObservedOutcome(market, sessionDate, open, high, low, close,
            recordedCandles, lastCandleAtUtc,
            morning?.PreviousHigh is decimal previousHigh ? high > previousHigh : (bool?)null,
            morning?.PreviousLow is decimal previousLow ? low < previousLow : (bool?)null,
            direction, judgement, assessment, coverage, assessedAtUtc);
    }
}
