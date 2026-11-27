using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class BankNiftyFailedAuctionPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 9, 15, 0, TimeSpan.FromMinutes(330));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSessionExtremeRequiresNearBoundaryReclaimNotANewSessionExtreme(bool sell)
    {
        var bars = FailedBuy();
        bars.Add(Bar(14, 101m, 102m, 100m, 101.5m));
        Assert.NotNull(Check(bars, sell));
        // A valid earlier reclaim can enter below the failed high, preserving
        // pullback opportunities rather than requiring a chase to a new extreme.
        bars.Add(Bar(15, 101.5m, 103m, 101m, 102.5m));
        Assert.Null(Check(bars, sell));
        Assert.Null(Check(bars.ToArray(), sell)); // reconstructed/restarted state
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WickAndOrdinaryPullbackAreNotFullCandleFailures(bool sell)
    {
        var bars = FailedBuy();
        bars[13] = Bar(13, 104m, 104.5m, 98m, 103m);
        bars.Add(Bar(14, 103m, 103.5m, 100.5m, 101m));
        Assert.Null(Check(bars, sell));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WickBeyondSessionExtremeIsNotABreakoutClose(bool sell)
    {
        var bars = FailedBuy();
        bars[12] = Bar(12, 101m, 105m, 101m, 102m);
        bars[13] = Bar(13, 102m, 102m, 99.5m, 100m);
        bars.Add(Bar(14, 100m, 101m, 99.5m, 100.5m));
        Assert.Null(Check(bars, sell));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedNewExtremeRetiresTheEarlierFailure(bool sell)
    {
        var bars = FailedBuy();
        bars.Add(Bar(14, 101m, 106m, 100m, 106m));
        bars.Add(Bar(15, 106m, 106m, 103m, 104m));
        Assert.Null(Check(bars, sell));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OppositeEntryNeedsTwoAcceptedClosesAndCannotReuseLostAcceptance(bool sell)
    {
        var bars = FailedBuy();
        bars.Add(Bar(14, 101m, 102m, 100m, 101m));
        bars.Add(Bar(15, 101m, 101.5m, 100m, 100.5m));
        bars.Add(Bar(16, 100.5m, 100.5m, 99.5m, 99.5m));
        Assert.NotNull(Check(bars, sell, opposite: true));
        bars.Add(Bar(17, 99.5m, 99.8m, 98.8m, 99m));
        Assert.Null(Check(bars, sell, opposite: true));
        bars.Add(Bar(18, 99m, 101m, 98.7m, 99.2m));
        Assert.Null(Check(bars, sell, opposite: true)); // wick across acceptance
        bars.Add(Bar(19, 99.2m, 101m, 99.2m, 100.5m));
        bars.Add(Bar(20, 100.5m, 100.5m, 99.7m, 99.8m));
        Assert.NotNull(Check(bars, sell, opposite: true));
        bars.Add(Bar(21, 99.8m, 99.8m, 98m, 98m));
        bars.Add(Bar(22, 98m, 98m, 97.5m, 97.5m));
        Assert.Null(Check(bars, sell, opposite: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingIntervalsAndNewSessionsCannotInventFailureMemory(bool sell)
    {
        var bars = FailedBuy();
        bars[13] = bars[13] with { OpenTimeUtc = Start.AddMinutes(14) };
        bars.Add(Bar(15, 101m, 102m, 100m, 101.5m));
        Assert.Null(Check(bars, sell));
        bars = FailedBuy();
        bars.Add(Bar(14, 101m, 102m, 100m, 101.5m) with { OpenTimeUtc = Start.AddDays(1) });
        Assert.Null(Check(bars, sell));
    }

    private static List<StrategyPriceBar> FailedBuy() =>
        [.. Enumerable.Range(0, 12).Select(i => Bar(i, 100m, 102m, 99m, 101m)),
            Bar(12, 102m, 105m, 102m, 104m), Bar(13, 104m, 104.5m, 100m, 101m)];

    private static string? Check(IEnumerable<StrategyPriceBar> bars, bool sell, bool opposite = false) =>
        BankNiftyFailedAuctionPolicy.Rejection(bars.Select(b => sell
            ? b with { Open = 200m - b.Open, High = 200m - b.Low, Low = 200m - b.High, Close = 200m - b.Close }
            : b).ToArray(), sell ^ opposite ? Direction.Sell : Direction.Buy);

    private static StrategyPriceBar Bar(int minute, decimal open, decimal high, decimal low, decimal close) =>
        new(Start.AddMinutes(minute), open, high, low, close);
}
