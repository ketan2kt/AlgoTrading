using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class ExpiryAwareOptionPolicyTests
{
    [Fact]
    public void ExpiryDayTightensStopAndReducesLots()
    {
        var date = new DateOnly(2026, 8, 13);

        Assert.Equal(8m, ExpiryAwareOptionPolicy.StopLossPercent(date, date, 10m));
        Assert.Equal(2, ExpiryAwareOptionPolicy.MaximumLots(date, date, 5));
    }
}
