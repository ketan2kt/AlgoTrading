using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;

namespace TradingSystem.UnitTests;

public sealed class HeroZeroCandidatePolicyTests
{
    [Fact]
    public void SelectBalancesPremiumLiquidityOiAndVolume()
    {
        var expiry = new DateOnly(2026, 8, 26);
        var selected = HeroZeroCandidatePolicy.Select([
            Candidate("NIFTY-A", "CE", 20m, 19.8m, 20.2m, 5000m, 3000m, 200m, expiry),
            Candidate("NIFTY-B", "CE", 19m, 16m, 22m, 20000m, 10000m, 500m, expiry)
        ], "CE", 20m, 15m);

        Assert.NotNull(selected);
        Assert.Equal("NIFTY-A", selected.Symbol);
    }

    [Fact]
    public void SelectUsesValidLtpVolumeAndOiWhenMarketDepthIsUnavailable()
    {
        var expiry = new DateOnly(2026, 8, 26);
        var selected = HeroZeroCandidatePolicy.Select([
            Candidate("NO-DEPTH", "PE", 20m, 0m, 0m, 1000m, 1000m, 1m, expiry),
            Candidate("WIDE", "PE", 20m, 10m, 30m, 1000m, 1000m, 1m, expiry)
        ], "PE", 20m, 15m);

        Assert.NotNull(selected);
        Assert.Equal("NO-DEPTH", selected.Symbol);
    }

    [Fact]
    public void SelectRejectsOneSidedOrWideMarketDepth()
    {
        var expiry = new DateOnly(2026, 8, 26);
        var selected = HeroZeroCandidatePolicy.Select([
            Candidate("ONE-SIDED", "PE", 20m, 0m, 20m, 1000m, 1000m, 1m, expiry),
            Candidate("WIDE", "PE", 20m, 10m, 30m, 1000m, 1000m, 1m, expiry)
        ], "PE", 20m, 15m);

        Assert.Null(selected);
    }

    [Fact]
    public void ExpansionRejectsAnAverageSizedCandle()
    {
        var bars = Bars().Append(new StrategyPriceBar(DateTimeOffset.UtcNow,
            100m, 105m, 99m, 104m)).ToArray();

        var result = HeroZeroExpansionPolicy.Evaluate(bars);

        Assert.False(result.Permitted);
    }

    [Fact]
    public void ExpansionRequiresDecisiveBreakBeyondPriorStructure()
    {
        var bars = Bars().Append(new StrategyPriceBar(DateTimeOffset.UtcNow,
            103m, 112m, 102m, 111m)).ToArray();

        var result = HeroZeroExpansionPolicy.Evaluate(bars);

        Assert.True(result.Permitted);
    }

    private static HeroZeroCandidateInput Candidate(string symbol, string type, decimal premium,
        decimal bid, decimal ask, decimal volume, decimal oi, decimal oiChange, DateOnly expiry) =>
        new(Guid.NewGuid(), symbol, type, 25000m, expiry, 65, premium, bid, ask, volume, oi, oiChange);

    private static IEnumerable<StrategyPriceBar> Bars() => Enumerable.Range(0, 9).Select(index =>
        new StrategyPriceBar(DateTimeOffset.UtcNow.AddMinutes(index - 10),
            100m + index * .2m, 105m + index * .2m, 99m + index * .2m,
            103m + index * .2m));
}
