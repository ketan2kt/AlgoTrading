using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class LiveExecutionIntentTests
{
    [Fact]
    public void PartialPreparationIsDistinctFromAmbiguousSellSubmission()
    {
        var intent = NewIntent();
        intent.Filled(100, 100m);
        intent.Protected("stop", DateTimeOffset.UtcNow);
        intent.BeginScaleAction("PartialProtectionResizing");
        Assert.Throws<InvalidOperationException>(() => intent.RecordPartialExit(50, DateTimeOffset.UtcNow));
        intent.BeginPartialSubmission();
        intent.RecoverUnsentPartial();
        Assert.Equal("PartialProtectionResizing", intent.Status);
        intent.RetainProtectedRemainder("Restored after restart");
        intent.AdjustProtectedExposure(100, 95m, DateTimeOffset.UtcNow);
        intent.BeginFinalExit();
        Assert.Equal("FinalExitPending", intent.Status);
    }
    [Fact]
    public void DefiniteBrokerRejectionDoesNotRemainReconciliationRequired()
    {
        var intent = NewIntent();
        intent.RequireReconciliation("Groww execution API returned 401.");

        intent.Reject(intent.LastError!, DateTimeOffset.UtcNow);

        Assert.Equal("Rejected", intent.Status);
        Assert.Equal("Groww execution API returned 401.", intent.LastError);
    }

    private static LiveExecutionIntent NewIntent() => new(Guid.NewGuid(), "SENSEX", Guid.NewGuid(),
        "SENSEX", Guid.NewGuid(), Direction.Buy, 100, 100m, 90m, 110m,
        "LE12345678", DateTimeOffset.UtcNow);

    [Fact]
    public void FinalExitCannotSubmitTwiceAndTracksOnlyTheVerifiedRemainder()
    {
        var intent = NewIntent();
        intent.Filled(100, 101m);
        intent.Protected("first-stop", DateTimeOffset.UtcNow);
        intent.BeginFinalExit();
        intent.BeginFinalSell();
        Assert.Throws<InvalidOperationException>(intent.BeginFinalSell);
        intent.ReserveResidualProtection(40);
        Assert.Equal(40, intent.FilledQuantity);
        Assert.Equal("ResidualStopSubmitting", intent.Status);
        intent.ResidualProtected("recovery-stop", DateTimeOffset.UtcNow);
        Assert.Equal("ResidualProtected", intent.Status);
        Assert.Equal("recovery-stop", intent.ProtectionId);
        intent.Closed(DateTimeOffset.UtcNow);
        Assert.Equal(0, intent.FilledQuantity);
    }

    [Fact]
    public void PartialFillMismatchCanStillTrailAndExitTheActualQuantity()
    {
        var intent = NewIntent();
        intent.Filled(100, 101m);
        intent.Protected("broker-stop", DateTimeOffset.UtcNow);
        intent.BeginScaleAction("PartialSubmitting");
        intent.RecordPartialExit(20, DateTimeOffset.UtcNow);
        intent.RetainProtectedRemainder("Only 20 sold; verified stop on 80");
        intent.AdjustProtectedExposure(80, 95m, DateTimeOffset.UtcNow);
        Assert.Equal("ProtectedScaleMismatch", intent.Status);
        Assert.Equal(80, intent.FilledQuantity);
        intent.BeginFinalExit();
        Assert.Equal("FinalExitPending", intent.Status);
    }
}
