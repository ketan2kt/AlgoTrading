using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class PaperAutomationStateTests
{
    [Fact]
    public void TracksCurrentPremiumIndependentlyForConcurrentPositions()
    {
        var firstSignal = Guid.NewGuid();
        var secondSignal = Guid.NewGuid();
        var state = new PaperAutomationState(TimeProvider.System);

        state.SynchronizeActivePositions([firstSignal, secondSignal]);
        state.RecordPositionMark(firstSignal, 72.75m, 72.60m, 0m, true);
        state.RecordPositionMark(secondSignal, 104.30m, 104.10m, 325m, true);

        var marks = state.GetCurrent().ActivePositionMarks!.ToDictionary(value => value.SignalId);
        Assert.Equal(72.75m, marks[firstSignal].CurrentPrice);
        Assert.Equal(104.30m, marks[secondSignal].CurrentPrice);
        Assert.Equal(72.60m, marks[firstSignal].ExecutablePrice);
        Assert.Equal(104.10m, marks[secondSignal].ExecutablePrice);
    }

    [Fact]
    public void RemovesMarksForPositionsThatAreNoLongerOpen()
    {
        var closedSignal = Guid.NewGuid();
        var activeSignal = Guid.NewGuid();
        var state = new PaperAutomationState(TimeProvider.System);
        state.SynchronizeActivePositions([closedSignal, activeSignal]);
        state.RecordPositionMark(closedSignal, 50m, 49.90m, -10m, true);
        state.RecordPositionMark(activeSignal, 75m, 74.90m, 10m, true);

        state.SynchronizeActivePositions([activeSignal]);

        var mark = Assert.Single(state.GetCurrent().ActivePositionMarks!);
        Assert.Equal(activeSignal, mark.SignalId);
    }
}
