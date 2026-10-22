using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.UnitTests;

public sealed class HeroZeroCandidatePolicyTests
{
    [Fact]
    public void DefaultsUseLateExpansionWindowAndMarketSpecificAtmCaps()
    {
        var options = new HeroZeroOptions();

        Assert.Equal("14:25", options.EntryWindowStart);
        Assert.Equal("15:00", options.EntryCutoff);
        Assert.Equal(100m, options.NiftyMaximumStrikeDistance);
        Assert.Equal(250m, options.SensexMaximumStrikeDistance);
        Assert.False(IndexEntryWindowPolicy.AllowsEntry(
            new TimeOnly(14, 24, 59), new TimeOnly(14, 25), new TimeOnly(15, 0)));
        Assert.True(IndexEntryWindowPolicy.AllowsEntry(
            new TimeOnly(14, 25), new TimeOnly(14, 25), new TimeOnly(15, 0)));
    }

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

    [Theory]
    [InlineData("CE", 25100, "NEAR-CE")]
    [InlineData("PE", 24900, "NEAR-PE")]
    public void SelectKeepsHeroZeroOnTheOtmSideAndWithinTheAtmDistance(
        string optionType, decimal nearStrike, string expectedSymbol)
    {
        var expiry = new DateOnly(2026, 8, 26);
        var selected = HeroZeroCandidatePolicy.Select([
            Candidate(expectedSymbol, optionType, 20m, 19.8m, 20.2m, 5000m, 3000m, 200m,
                expiry, nearStrike),
            Candidate("TOO-FAR", optionType, 20m, 19.8m, 20.2m, 50000m, 30000m, 2000m,
                expiry, optionType == "CE" ? 25150m : 24850m),
            Candidate("WRONG-SIDE", optionType, 20m, 19.8m, 20.2m, 50000m, 30000m, 2000m,
                expiry, optionType == "CE" ? 24950m : 25050m)
        ], optionType, 20m, 15m, 25000m, 100m);

        Assert.NotNull(selected);
        Assert.Equal(expectedSymbol, selected.Symbol);
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
        decimal bid, decimal ask, decimal volume, decimal oi, decimal oiChange, DateOnly expiry,
        decimal strike = 25000m) =>
        new(Guid.NewGuid(), symbol, type, strike, expiry, 65, premium, bid, ask, volume, oi, oiChange);

    private static IEnumerable<StrategyPriceBar> Bars() => Enumerable.Range(0, 9).Select(index =>
        new StrategyPriceBar(DateTimeOffset.UtcNow.AddMinutes(index - 10),
            100m + index * .2m, 105m + index * .2m, 99m + index * .2m,
            103m + index * .2m));
}
