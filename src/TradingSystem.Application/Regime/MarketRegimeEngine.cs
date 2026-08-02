using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Regime;

public sealed class MarketRegimeEngine(MarketRegimeOptions options)
{
    public MarketRegimeResult Evaluate(MarketRegimeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);
        var support = new List<string>();
        var contradict = new List<string>();
        if (!input.MarketDataTradingPermitted || input.DataQuality < options.MinimumDataQuality)
        {
            if (!input.MarketDataTradingPermitted) contradict.Add("Market-data health blocks trading.");
            if (input.DataQuality < options.MinimumDataQuality) contradict.Add("Data quality is below threshold.");
            return Result(MarketRegime.Uncertain, null, 0m, support, contradict, input, false);
        }

        var gapPercent = Percent(input.OpeningPrice - input.PreviousClose, input.PreviousClose);
        var emaSpread = Percent(input.FastEma - input.SlowEma, input.CurrentPrice);
        var aboveVwap = input.CurrentPrice > input.Vwap;
        var confidence = 0.55m + Math.Min(Math.Abs(emaSpread), 1m) * 0.20m +
                         Math.Min(Math.Max(input.RelativeVolume - 1m, 0m), 1m) * 0.10m;

        if (gapPercent >= options.GapThresholdPercent)
        {
            support.Add($"Gap up {gapPercent:F2}%.");
            if (input.CurrentPrice > input.OpeningRangeHigh && aboveVwap)
            {
                support.Add("Price holds above opening range and VWAP.");
                return Result(MarketRegime.GapUpContinuation, Direction.Buy, confidence + 0.10m,
                    support, contradict, input);
            }
            if (input.CurrentPrice < input.OpeningPrice && !aboveVwap)
            {
                support.Add("Gap is rejected below open and VWAP.");
                return Result(MarketRegime.GapUpRejection, Direction.Sell, confidence,
                    support, contradict, input);
            }
            contradict.Add("Gap-up confirmation is incomplete.");
        }
        else if (gapPercent <= -options.GapThresholdPercent)
        {
            support.Add($"Gap down {Math.Abs(gapPercent):F2}%.");
            if (input.CurrentPrice < input.OpeningRangeLow && !aboveVwap)
            {
                support.Add("Price holds below opening range and VWAP.");
                return Result(MarketRegime.GapDownContinuation, Direction.Sell, confidence + 0.10m,
                    support, contradict, input);
            }
            if (input.CurrentPrice > input.OpeningPrice && aboveVwap)
            {
                support.Add("Gap is reversing above open and VWAP.");
                return Result(MarketRegime.GapDownReversal, Direction.Buy, confidence,
                    support, contradict, input);
            }
            contradict.Add("Gap-down confirmation is incomplete.");
        }

        if (input.AtrPercent >= options.HighAtrPercent &&
            input.RelativeVolume >= options.ExpansionRelativeVolume)
        {
            support.Add("ATR and relative volume confirm volatility expansion.");
            Direction? bias = emaSpread > options.WeakTrendEmaSpreadPercent ? Direction.Buy :
                emaSpread < -options.WeakTrendEmaSpreadPercent ? Direction.Sell : null;
            return Result(MarketRegime.HighVolatilityExpansion, bias, confidence,
                support, contradict, input);
        }

        if (emaSpread >= options.StrongTrendEmaSpreadPercent && aboveVwap)
        {
            support.Add("Fast EMA leads slow EMA and price is above VWAP.");
            return Result(MarketRegime.StrongBullishTrend, Direction.Buy, confidence + 0.10m,
                support, contradict, input);
        }
        if (emaSpread <= -options.StrongTrendEmaSpreadPercent && !aboveVwap)
        {
            support.Add("Fast EMA trails slow EMA and price is below VWAP.");
            return Result(MarketRegime.StrongBearishTrend, Direction.Sell, confidence + 0.10m,
                support, contradict, input);
        }
        if (emaSpread >= options.WeakTrendEmaSpreadPercent)
        {
            support.Add("EMA structure has a modest bullish slope.");
            if (!aboveVwap) contradict.Add("Price remains below VWAP.");
            return Result(MarketRegime.WeakBullishTrend, Direction.Buy, confidence,
                support, contradict, input);
        }
        if (emaSpread <= -options.WeakTrendEmaSpreadPercent)
        {
            support.Add("EMA structure has a modest bearish slope.");
            if (aboveVwap) contradict.Add("Price remains above VWAP.");
            return Result(MarketRegime.WeakBearishTrend, Direction.Sell, confidence,
                support, contradict, input);
        }
        if (input.AtrPercent <= options.LowAtrPercent)
        {
            support.Add("ATR indicates volatility compression.");
            return Result(MarketRegime.LowVolatilityCompression, null, confidence,
                support, contradict, input);
        }

        support.Add("EMA spread is neutral without a confirmed directional break.");
        return Result(MarketRegime.RangeBound, null, confidence, support, contradict, input);
    }

    private MarketRegimeResult Result(MarketRegime regime, Direction? bias, decimal confidence,
        IReadOnlyList<string> support, IReadOnlyList<string> contradict, MarketRegimeInput input,
        bool? permitted = null)
    {
        var bounded = Math.Clamp(confidence, 0m, 1m);
        return new(regime, bias, bounded, support.ToArray(), contradict.ToArray(), input.DataQuality,
            permitted ?? bounded >= options.MinimumTradingConfidence, input.ObservedAtUtc.ToUniversalTime());
    }

    private static decimal Percent(decimal difference, decimal basis) => difference / basis * 100m;

    private static void Validate(MarketRegimeInput input)
    {
        if (input.TradingSessionId == Guid.Empty || input.CurrentPrice <= 0 || input.PreviousClose <= 0 ||
            input.OpeningPrice <= 0 || input.OpeningRangeHigh < input.OpeningRangeLow || input.Vwap <= 0 ||
            input.FastEma <= 0 || input.SlowEma <= 0 || input.AtrPercent < 0 ||
            input.RelativeVolume < 0 || input.DataQuality is < 0 or > 1)
            throw new ArgumentException("Market-regime input is invalid.", nameof(input));
    }
}
