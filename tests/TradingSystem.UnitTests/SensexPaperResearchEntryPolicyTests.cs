using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexPaperResearchEntryPolicyTests
{
    [Fact]
    public void DerivesCandidateFromQualifiedUnifiedLifecycle()
    {
        var lifecycle = Lifecycle(SensexSetupPhase.EntryWindow, Direction.Buy, .09m, .33m);

        var candidate = SensexPaperResearchEntryPolicy.DeriveLifecycleCandidate(lifecycle);

        Assert.Equal(Direction.Buy, candidate.Direction);
        Assert.True(candidate.Confidence >= .70m);
    }

    [Fact]
    public void AllowsHighConfidenceAlignedEntryWindowForPaperResearch()
    {
        var lifecycle = Lifecycle(SensexSetupPhase.EntryWindow, Direction.Buy, -.28m, .8m);

        var result = SensexPaperResearchEntryPolicy.Evaluate(Direction.Buy, .72m,
            lifecycle, false);

        Assert.True(result.Permitted);
    }

    [Theory]
    [InlineData(.69, false)]
    [InlineData(.72, true)]
    public void RejectsWeakOrOverlappingResearch(decimal confidence, bool active)
    {
        var lifecycle = Lifecycle(SensexSetupPhase.EntryWindow, Direction.Buy, 0m, .8m);

        var result = SensexPaperResearchEntryPolicy.Evaluate(Direction.Buy, confidence,
            lifecycle, active);

        Assert.False(result.Permitted);
    }

    [Fact]
    public void RejectsExtendedCandidate()
    {
        var lifecycle = Lifecycle(SensexSetupPhase.EntryWindow, Direction.Sell, .70m, .8m);

        var result = SensexPaperResearchEntryPolicy.Evaluate(Direction.Sell, .75m,
            lifecycle, false);

        Assert.False(result.Permitted);
    }

    [Fact]
    public void StrongPersistentThesisCanLiftAlignedPullbackCandidate()
    {
        var lifecycle = Lifecycle(SensexSetupPhase.EntryWindow, Direction.Sell, -1.05m, .35m);
        var thesis = new PersistentDirectionalThesisV1(
            PersistentDirectionalThesisAnalyzerV1.Version,
            PersistentDirectionalThesisAnalyzerV1.Status,
            PersistentDirectionalThesisAnalyzerV1.PaperEligibleFrom,
            Direction.Sell, .90m, DirectionalThesisPhaseV1.Maintained,
            9, 1, true, false, false, []);

        var result = SensexPaperResearchEntryPolicy.Evaluate(Direction.Sell, .60m,
            lifecycle, false, thesis);

        Assert.True(result.Permitted);
        Assert.True(result.EffectiveConfidence >= .70m);
    }

    private static SensexSetupLifecycle Lifecycle(SensexSetupPhase phase, Direction direction,
        decimal extension, decimal separation) =>
        new(phase, direction, 15, extension, separation, []);
}
