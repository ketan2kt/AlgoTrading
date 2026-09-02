using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public sealed record MarketLocationDecision(bool Permitted, string Context,
    decimal? NearestSupport, decimal? NearestResistance, IReadOnlyList<string> Reasons);

/// <summary>Locates a candidate against data-derived session levels; levels are zones, not predictions.</summary>
public static class MarketLocationPolicy
{
    public static MarketLocationDecision Evaluate(IReadOnlyList<StrategyPriceBar> session,
        IReadOnlyList<StrategyPriceBar> previousSession, Direction direction, decimal atr,
        decimal openingRangeHigh, decimal openingRangeLow)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(previousSession);
        if (session.Count < 4 || previousSession.Count == 0 || atr <= 0)
            return new(false, "Unavailable", null, null,
                ["Major support/resistance context is incomplete."]);

        var latest = session[^1];
        var price = latest.Close;
        var previousHigh = previousSession.Max(x => x.High);
        var previousLow = previousSession.Min(x => x.Low);
        var previousClose = previousSession[^1].Close;
        var sessionPrior = session.SkipLast(1).ToArray();
        var structuralSupports = new[] { previousLow, previousClose, openingRangeLow,
            sessionPrior.Min(x => x.Low) };
        var structuralResistances = new[] { previousHigh, previousClose, openingRangeHigh,
            sessionPrior.Max(x => x.High) };
        var supportLevels = structuralSupports.Append(RoundLevel(price, false))
            .Where(level => level <= price).Distinct().ToArray();
        var resistanceLevels = structuralResistances.Append(RoundLevel(price, true))
            .Where(level => level >= price).Distinct().ToArray();
        var support = supportLevels.Length == 0 ? (decimal?)null : supportLevels.Max();
        var resistance = resistanceLevels.Length == 0 ? (decimal?)null : resistanceLevels.Min();
        var sessionLow = session.Min(x => x.Low);
        var sessionHigh = session.Max(x => x.High);
        var sessionRange = sessionHigh - sessionLow;
        var location = sessionRange <= 0 ? 0.5m : (price - sessionLow) / sessionRange;
        var reasons = new List<string>();

        var bullishRejection = BullishRejection(session);
        var bearishRejection = BearishRejection(session);
        var breakoutBarrier = new[] { previousHigh, openingRangeHigh,
            sessionPrior.Max(x => x.High) }.Max();
        var breakdownBarrier = new[] { previousLow, openingRangeLow,
            sessionPrior.Min(x => x.Low) }.Min();
        var aboveResistance = price > breakoutBarrier + atr * 0.10m;
        var belowSupport = price < breakdownBarrier - atr * 0.10m;
        var nearSupport = support is { } ns && price - ns <= atr * 0.35m;
        var nearResistance = resistance is { } nr && nr - price <= atr * 0.35m;

        if (direction == Direction.Buy && nearResistance && !aboveResistance)
            reasons.Add("Buy entry is too close to major resistance without a confirmed breakout.");
        if (direction == Direction.Sell && nearSupport && !belowSupport)
            reasons.Add("Sell entry is too close to major support without a confirmed breakdown.");
        if (direction == Direction.Buy && nearSupport && !bullishRejection)
            reasons.Add("Support is nearby, but bullish rejection has not been confirmed.");
        if (direction == Direction.Sell && nearResistance && !bearishRejection)
            reasons.Add("Resistance is nearby, but bearish rejection has not been confirmed.");

        var context = direction == Direction.Buy && nearSupport
            ? "SupportRejection"
            : direction == Direction.Sell && nearResistance
                ? "ResistanceRejection"
                : aboveResistance || belowSupport ? "ConfirmedBreakout" : "BetweenLevels";
        var gapAtr = Math.Abs(session[0].Open - previousClose) / atr;
        if (gapAtr >= 0.60m)
            context += session[0].Open < previousClose ? "+GapDown" : "+GapUp";
        if (location is > 0.35m and < 0.65m && context.StartsWith("BetweenLevels", StringComparison.Ordinal))
            reasons.Add("Price is in the middle of the current session range; location offers no clear edge.");

        return new(reasons.Count == 0, context, support, resistance, reasons);
    }

    private static bool BullishRejection(IReadOnlyList<StrategyPriceBar> bars)
    {
        var last = bars[^1];
        var body = Math.Max(Math.Abs(last.Close - last.Open), 0.01m);
        return last.Close > last.Open && last.Close >= last.Low + (last.High - last.Low) * 0.60m &&
               Math.Min(last.Open, last.Close) - last.Low >= body * 0.60m ||
               bars.Count >= 2 && last.Close > bars[^2].High;
    }

    private static bool BearishRejection(IReadOnlyList<StrategyPriceBar> bars)
    {
        var last = bars[^1];
        var body = Math.Max(Math.Abs(last.Close - last.Open), 0.01m);
        return last.Close < last.Open && last.Close <= last.Low + (last.High - last.Low) * 0.40m &&
               last.High - Math.Max(last.Open, last.Close) >= body * 0.60m ||
               bars.Count >= 2 && last.Close < bars[^2].Low;
    }

    private static decimal RoundLevel(decimal price, bool above)
    {
        var step = price >= 50_000m ? 500m : price >= 10_000m ? 100m : 50m;
        return (above ? Math.Ceiling(price / step) : Math.Floor(price / step)) * step;
    }
}
