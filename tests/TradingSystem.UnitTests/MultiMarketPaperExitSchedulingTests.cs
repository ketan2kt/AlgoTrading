using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.UnitTests;

public sealed class MultiMarketPaperExitSchedulingTests
{
    [Theory]
    [InlineData(false, false, false, false, false, true)]
    [InlineData(false, false, true, true, false, true)]
    [InlineData(false, false, true, false, false, false)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(false, false, false, false, true, false)]
    public void PartialDoesNotRaceAFullExitOnSameQuote(bool manual, bool stop,
        bool target, bool continuation, bool forced, bool expected)
    {
        Assert.Equal(expected, MultiMarketPaperTradingService.ShouldTakePartial(
            manual, stop, target, continuation, forced));
    }
}
