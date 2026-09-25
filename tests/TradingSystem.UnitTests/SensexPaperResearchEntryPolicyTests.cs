using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexPaperResearchEntryPolicyTests
{
    [Fact]
    public void AllowsHighConfidenceAlignedEntryWindowForPaperResearch()
    {
        var lifecycle = Lifecycle(SensexSetupPhase.EntryWindow, Direction.Buy, -.28m, .8m);

        var result = SensexPaperResearchEntryPolicy.Evaluate(Direction.Buy, .72m,
            lifecycle, 0, false);

        Assert.True(result.Permitted);
    }

    [Theory]
    [InlineData(.69, 0, false)]
    [InlineData(.72, 2, false)]
    [InlineData(.72, 0, true)]
    public void RejectsWeakCappedOrOverlappingResearch(decimal confidence,
        int entriesToday, bool active)
    {
        var lifecycle = Lifecycle(SensexSetupPhase.EntryWindow, Direction.Buy, 0m, .8m);

        var result = SensexPaperResearchEntryPolicy.Evaluate(Direction.Buy, confidence,
            lifecycle, entriesToday, active);

        Assert.False(result.Permitted);
    }

    [Fact]
    public void RejectsExtendedCandidate()
    {
        var lifecycle = Lifecycle(SensexSetupPhase.EntryWindow, Direction.Sell, .50m, .8m);

        var result = SensexPaperResearchEntryPolicy.Evaluate(Direction.Sell, .75m,
            lifecycle, 0, false);

        Assert.False(result.Permitted);
    }

    private static SensexSetupLifecycle Lifecycle(SensexSetupPhase phase, Direction direction,
        decimal extension, decimal separation) =>
        new(phase, direction, 15, extension, separation, []);
}
