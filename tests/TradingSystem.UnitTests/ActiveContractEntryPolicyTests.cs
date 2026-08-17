using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class ActiveContractEntryPolicyTests
{
    [Fact]
    public void RejectsSameActiveInstrumentAndDirection()
    {
        var instrumentId = Guid.NewGuid();
        var active = new[] { new ActiveContractExposure(instrumentId, Direction.Buy) };

        Assert.True(ActiveContractEntryPolicy.IsDuplicate(active, instrumentId, Direction.Buy));
    }

    [Fact]
    public void AllowsDifferentStrikeOrOppositeDirection()
    {
        var instrumentId = Guid.NewGuid();
        var active = new[] { new ActiveContractExposure(instrumentId, Direction.Buy) };

        Assert.False(ActiveContractEntryPolicy.IsDuplicate(active, Guid.NewGuid(), Direction.Buy));
        Assert.False(ActiveContractEntryPolicy.IsDuplicate(active, instrumentId, Direction.Sell));
    }
}
