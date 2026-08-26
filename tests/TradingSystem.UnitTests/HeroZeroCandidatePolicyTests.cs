using TradingSystem.Application.Execution;

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
    public void SelectRejectsZeroLiquidityAndWideSpread()
    {
        var expiry = new DateOnly(2026, 8, 26);
        var selected = HeroZeroCandidatePolicy.Select([
            Candidate("NO-BID", "PE", 20m, 0m, 20m, 1000m, 1000m, 1m, expiry),
            Candidate("WIDE", "PE", 20m, 10m, 30m, 1000m, 1000m, 1m, expiry)
        ], "PE", 20m, 15m);

        Assert.Null(selected);
    }

    private static HeroZeroCandidateInput Candidate(string symbol, string type, decimal premium,
        decimal bid, decimal ask, decimal volume, decimal oi, decimal oiChange, DateOnly expiry) =>
        new(Guid.NewGuid(), symbol, type, 25000m, expiry, 65, premium, bid, ask, volume, oi, oiChange);
}
