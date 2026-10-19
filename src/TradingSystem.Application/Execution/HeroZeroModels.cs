using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record HeroZeroCandidate(
    Guid InstrumentId, string Symbol, string OptionType, decimal Strike, DateOnly Expiry,
    int LotSize, decimal Premium, decimal Bid, decimal Ask, decimal SpreadPercent,
    decimal Volume, decimal OpenInterest, decimal OpenInterestChange, decimal Score);

public sealed record HeroZeroLegSnapshot(
    Guid PositionId, string Symbol, string OptionType, decimal Strike, int Quantity,
    decimal EntryPremium, decimal CurrentPremium, decimal StopPremium, string Status,
    decimal UnrealisedPnl, decimal? RealisedPnl);

public sealed record HeroZeroMonitorSnapshot(
    string Market, bool IsExpirySession, DateOnly? Expiry, string Status, string Explanation,
    DateTimeOffset ObservedAtUtc, decimal? SpotPrice, HeroZeroCandidate? CallCandidate,
    HeroZeroCandidate? PutCandidate, IReadOnlyList<HeroZeroLegSnapshot> ActiveLegs,
    decimal CombinedEntryCost, decimal CombinedCurrentValue, decimal CombinedPnl);

public interface IHeroZeroMonitorReader
{
    HeroZeroMonitorSnapshot GetSnapshot(string market);
}

public sealed record HeroZeroCandidateInput(
    Guid InstrumentId, string Symbol, string OptionType, decimal Strike, DateOnly Expiry,
    int LotSize, decimal Premium, decimal Bid, decimal Ask, decimal Volume,
    decimal OpenInterest, decimal OpenInterestChange);

public static class HeroZeroCandidatePolicy
{
    public static HeroZeroCandidate? Select(IReadOnlyCollection<HeroZeroCandidateInput> candidates,
        string optionType, decimal targetPremium, decimal maximumSpreadPercent)
    {
        var eligible = candidates.Where(value => value.OptionType == optionType && value.Premium > 0 &&
            value.OpenInterest > 0 && value.Volume > 0 &&
            ((value.Bid <= 0 && value.Ask <= 0) || (value.Bid > 0 && value.Ask >= value.Bid)))
            .Select(value => new
            {
                Value = value,
                HasMarketDepth = value.Bid > 0 && value.Ask > 0,
                Spread = value.Bid > 0 && value.Ask > 0
                    ? (value.Ask - value.Bid) / value.Premium * 100m : 0m
            }).Where(value => !value.HasMarketDepth || value.Spread <= maximumSpreadPercent).ToArray();
        if (eligible.Length == 0) return null;
        var maxOi = eligible.Max(value => value.Value.OpenInterest);
        var maxVolume = eligible.Max(value => value.Value.Volume);
        return eligible.Select(value =>
        {
            var premiumFit = Math.Max(0m, 1m - Math.Abs(value.Value.Premium - targetPremium) /
                Math.Max(targetPremium, 1m));
            // Groww index-option quotes can omit market depth while still returning valid
            // LTP, volume and OI. Missing depth is neutral; an observed wide spread still rejects.
            var liquidity = value.HasMarketDepth
                ? 1m - value.Spread / maximumSpreadPercent : .5m;
            var oi = value.Value.OpenInterest / maxOi;
            var volume = value.Value.Volume / maxVolume;
            var oiChange = value.Value.OpenInterestChange > 0 ? 1m : 0m;
            var score = premiumFit * 0.30m + liquidity * 0.20m + oi * 0.20m +
                        volume * 0.20m + oiChange * 0.10m;
            return new HeroZeroCandidate(value.Value.InstrumentId, value.Value.Symbol,
                value.Value.OptionType, value.Value.Strike, value.Value.Expiry, value.Value.LotSize,
                value.Value.Premium, value.Value.Bid, value.Value.Ask, value.Spread,
                value.Value.Volume, value.Value.OpenInterest, value.Value.OpenInterestChange, score);
        }).OrderByDescending(value => value.Score).ThenBy(value => Math.Abs(value.Premium - targetPremium))
          .First();
    }
}

public sealed record HeroZeroExpansionDecision(bool Permitted, Direction? Direction,
    IReadOnlyList<string> Reasons);

public static class HeroZeroExpansionPolicy
{
    public static HeroZeroExpansionDecision Evaluate(IReadOnlyList<StrategyPriceBar> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 10)
            return Reject("At least ten completed five-minute candles are required.");
        var prior = source.SkipLast(1).TakeLast(9).ToArray();
        var latest = source[^1];
        var baselineRange = prior.Average(value => value.High - value.Low);
        var latestRange = latest.High - latest.Low;
        var body = Math.Abs(latest.Close - latest.Open);
        if (baselineRange <= 0m || latestRange < baselineRange * 1.20m)
            return Reject("The latest candle is not a genuine 1.20x range expansion.");
        if (body < baselineRange * .60m)
            return Reject("The expansion body is below 0.60x the recent average range.");

        var closeLocation = latestRange <= 0m ? .5m : (latest.Close - latest.Low) / latestRange;
        var bullish = latest.Close > prior.Max(value => value.High) && closeLocation >= .75m;
        var bearish = latest.Close < prior.Min(value => value.Low) && closeLocation <= .25m;
        if (!bullish && !bearish)
            return Reject("The expansion did not close decisively beyond prior nine-candle structure.");
        var direction = bullish ? Direction.Buy : Direction.Sell;
        return new(true, direction,
            [$"Confirmed {direction} expiry expansion: range {latestRange / baselineRange:F2}x baseline.",
             $"Close location is {closeLocation:P0} of the candle range."]);
    }

    private static HeroZeroExpansionDecision Reject(string reason) => new(false, null, [reason]);
}
