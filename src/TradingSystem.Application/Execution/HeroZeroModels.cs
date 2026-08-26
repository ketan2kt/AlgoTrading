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
            value.Bid > 0 && value.Ask >= value.Bid && value.OpenInterest > 0 && value.Volume > 0)
            .Select(value => new
            {
                Value = value,
                Spread = (value.Ask - value.Bid) / value.Premium * 100m
            }).Where(value => value.Spread <= maximumSpreadPercent).ToArray();
        if (eligible.Length == 0) return null;
        var maxOi = eligible.Max(value => value.Value.OpenInterest);
        var maxVolume = eligible.Max(value => value.Value.Volume);
        return eligible.Select(value =>
        {
            var premiumFit = Math.Max(0m, 1m - Math.Abs(value.Value.Premium - targetPremium) /
                Math.Max(targetPremium, 1m));
            var liquidity = 1m - value.Spread / maximumSpreadPercent;
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
