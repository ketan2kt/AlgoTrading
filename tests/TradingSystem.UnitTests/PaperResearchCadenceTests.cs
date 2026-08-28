using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class PaperResearchCadenceTests
{
    [Fact]
    public void CapturesEarlyAndLatePostExitOutcomes()
    {
        Assert.Equal([5, 15, 30, 60], PaperResearchCadence.PostExitHorizonMinutes);
    }
}
