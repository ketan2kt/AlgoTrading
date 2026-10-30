using System.Text.Json;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Risk;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class LiveExecutionDiscoveryContractTests
{
    [Fact]
    public void MarketArmStatusIsIndependentAndOffByDefault()
    {
        var switchedAt = DateTimeOffset.UtcNow;
        var status = new LiveTradingArmStatus(true,
            [new("NIFTY", true, switchedAt, "admin"),
             new("SENSEX", false, null, null),
             new("BANK-NIFTY", false, null, null)],
            true, false, ["NIFTY", "SENSEX", "BANK-NIFTY"],
            null, null, null, null);

        Assert.True(status.ForMarket("NIFTY")!.Armed);
        Assert.False(status.ForMarket("SENSEX")!.Armed);
        Assert.False(status.ForMarket("BANK-NIFTY")!.Armed);
        Assert.Null(status.ForMarket("BANKEX"));
        Assert.Null(status.ForMarket("UNKNOWN"));
    }

    [Fact]
    public async Task PaperEntryWakeSignalsAreCoalescedWithoutLosingTheNextCycle()
    {
        using var wake = new LiveExecutionWakeSignal();
        wake.Notify();
        wake.Notify();

        Assert.True(await wake.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await wake.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public void OnlyAnExactlyFilledFinalOrderCanRecoverAClosedLeg()
    {
        var order = new BrokerOrderSnapshot("LX1234567890", "broker-exit", Guid.NewGuid(),
            Direction.Sell, 100, 100, 95m, OrderState.Filled, DateTimeOffset.UtcNow);

        Assert.True(AutomaticLiveExecutionService.IsConfirmedFinalFill(order, 100));
        Assert.False(AutomaticLiveExecutionService.IsConfirmedFinalFill(order with
        { FilledQuantity = 50, State = OrderState.PartiallyFilled }, 100));
        Assert.False(AutomaticLiveExecutionService.IsConfirmedFinalFill(order, 125));
        Assert.False(AutomaticLiveExecutionService.IsConfirmedFinalFill(null, 100));
        var stop = order with { ClientReference = "LP1234567890", FilledQuantity = 10 };
        Assert.True(AutomaticLiveExecutionService.HasConflictingExitFills(order, stop));
        Assert.False(AutomaticLiveExecutionService.HasConflictingExitFills(null, stop));
        Assert.False(AutomaticLiveExecutionService.HasConflictingExitFills(order,
            stop with { FilledQuantity = 0 }));
    }

    [Fact]
    public void LiveExecutionHasOneRequiredPaperParityMode()
    {
        Assert.Equal("PaperFillAndLifecycle", new LiveExecutionOptions().PaperParityMode);
    }

    [Fact]
    public void NiftyDiscoveryUsesDurableFilledEntryReference()
    {
        var signalId = Guid.NewGuid();

        Assert.True(AutomaticLiveExecutionService.TryParseNiftyEntrySignalId(
            "OrderFilled", $"{signalId:N}-ENTRY", out var parsed));
        Assert.Equal(signalId, parsed);
        Assert.False(AutomaticLiveExecutionService.TryParseNiftyEntrySignalId(
            "OrderSubmitted", $"{signalId:N}-ENTRY", out _));
        Assert.False(AutomaticLiveExecutionService.TryParseNiftyEntrySignalId(
            "OrderFilled", $"{signalId:N}-EXIT", out _));
    }

    [Fact]
    public void NiftyDiscoveryReadsCanonicalPaperProposalFields()
    {
        var instrumentId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new
        {
            optionProposal = new
            {
                instrumentId,
                proposedEntry = 100.25m,
                proposedStopLoss = 90.25m,
                proposedTarget = 110.25m
            }
        });

        Assert.True(AutomaticLiveExecutionService.TryReadOptionProposal(json, out var proposal));
        Assert.Equal(instrumentId, proposal.InstrumentId);
        Assert.Equal(100.25m, proposal.EntryPrice);
        Assert.Equal(90.25m, proposal.StopLoss);
        Assert.Equal(110.25m, proposal.Target);
    }

    [Theory]
    [InlineData("{\"cumulativeFilledQuantity\":250}", 250)]
    [InlineData("{\"cumulativeFilledQuantity\":75}", 75)]
    public void LiveQuantityCanBeTakenFromTheActualPaperFill(string json, int expected)
    {
        Assert.True(AutomaticLiveExecutionService.TryReadPaperFilledQuantity(json, out var quantity));
        Assert.Equal(expected, quantity);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"cumulativeFilledQuantity\":0}")]
    [InlineData("{\"cumulativeFilledQuantity\":-1}")]
    [InlineData("not-json")]
    public void InvalidPaperFillQuantityIsNeverEligibleForLive(string json)
    {
        Assert.False(AutomaticLiveExecutionService.TryReadPaperFilledQuantity(json, out var quantity));
        Assert.Equal(0, quantity);
    }

    [Fact]
    public void ExpansionPairNeedsBrokerCompatibleFloorAndOneRupeeIsEligible()
    {
        Assert.False(AutomaticLiveExecutionService.IsLiveBrokerProtectable(
            "Expansion Pair|pair-id|CE", .05m));
        Assert.True(AutomaticLiveExecutionService.IsLiveBrokerProtectable(
            "Expansion Pair|pair-id|PE", 1m));
        Assert.True(AutomaticLiveExecutionService.IsLiveBrokerProtectable(
            "Hero Zero|pair-id|CE", 10m));
        Assert.True(AutomaticLiveExecutionService.IsLiveBrokerProtectable(
            "Regular Sensex", 90m));
        Assert.False(AutomaticLiveExecutionService.IsLiveBrokerProtectable(
            "PaperOnly|persistent-directional-thesis-v1|sensex", 500m));
    }

    [Fact]
    public void PairedLegRequiresItsOppositePaperLeg()
    {
        Assert.True(AutomaticLiveExecutionService.TryOtherPairedLeg(
            "Hero Zero|pair-id|CE", out var put));
        Assert.Equal("Hero Zero|pair-id|PE", put);
        Assert.True(AutomaticLiveExecutionService.TryOtherPairedLeg(
            "Hero Zero|pair-id|PE", out var call));
        Assert.Equal("Hero Zero|pair-id|CE", call);
        Assert.True(AutomaticLiveExecutionService.TryOtherPairedLeg(
            "Expansion Pair|pair-id|CE", out var expansionPut));
        Assert.Equal("Expansion Pair|pair-id|PE", expansionPut);
        Assert.False(AutomaticLiveExecutionService.TryOtherPairedLeg(
            "Regular Sensex", out _));
    }

    [Fact]
    public void PaperLifecycleActionsKeepLiveExposureAndCountersConsistent()
    {
        var now = DateTimeOffset.UtcNow;
        var intent = new LiveExecutionIntent(Guid.NewGuid(), "NIFTY", Guid.NewGuid(),
            "NIFTY", Guid.NewGuid(), Direction.Buy, 100, 100m, 90m, 150m,
            "LE1234567890", now);
        intent.BeginEntrySubmission();
        Assert.Equal("EntrySubmitting", intent.Status);
        intent.Submitted("entry-1", now);
        intent.Filled(100, 100m);
        intent.Protected("stop-1", now);

        intent.RecordAddOn(25, now);
        intent.RecordPartialExit(50, now);
        intent.AdjustProtectedExposure(75, 105m, now);

        Assert.Equal(75, intent.FilledQuantity);
        Assert.Equal(25, intent.MirroredAddOnQuantity);
        Assert.Equal(50, intent.MirroredPartialExitQuantity);
        Assert.Equal(105m, intent.StopLoss);
        intent.FlagParityMismatch("Broker filled fewer units than paper.");
        Assert.NotNull(intent.LastError);
        intent.Closed(now);
        Assert.Equal(0, intent.FilledQuantity);
        Assert.Null(intent.LastError);
    }

    [Fact]
    public void PaperActionAndStopSnapshotsCanBeReadWithoutRecalculatingStrategy()
    {
        Assert.True(AutomaticLiveExecutionService.TryReadPaperAction(
            "{\"cumulativeFilledQuantity\":50,\"averageFillPrice\":123.45}", out var action));
        Assert.Equal(50, action.Quantity);
        Assert.Equal(123.45m, action.Price);
        Assert.True(AutomaticLiveExecutionService.TryReadDesiredStop(
            "{\"effectiveStop\":101.25}", out var stop));
        Assert.Equal(101.25m, stop);
        Assert.False(AutomaticLiveExecutionService.TryReadPaperAction("{}", out _));
        Assert.False(AutomaticLiveExecutionService.TryReadDesiredStop("{}", out _));
    }

    [Fact]
    public void ReconciliationIgnoresUnmanagedBrokerPositions()
    {
        var unmanagedInstrument = Guid.NewGuid();

        var result = AutomaticLiveExecutionService.ReconcileManagedPositions([],
            [new BrokerPositionSnapshot(unmanagedInstrument, Direction.Buy, 650, 100m)],
            DateTimeOffset.UtcNow);

        Assert.True(result.TradingPermitted);
        Assert.Empty(result.Mismatches);
    }

    [Fact]
    public void ReconciliationStillBlocksMismatchInManagedInstrument()
    {
        var managedInstrument = Guid.NewGuid();
        var result = AutomaticLiveExecutionService.ReconcileManagedPositions(
            [new ExpectedBrokerPosition(managedInstrument, Direction.Buy, 100)],
            [new BrokerPositionSnapshot(managedInstrument, Direction.Buy, 150, 100m)],
            DateTimeOffset.UtcNow);

        Assert.False(result.TradingPermitted);
        Assert.Single(result.Mismatches);
    }

    [Fact]
    public void NewLiveEntryRejectsUnmanagedSameContractExposureButNotOtherContracts()
    {
        var instrument = Guid.NewGuid();
        var otherInstrument = Guid.NewGuid();
        var managed = new[] { new ExpectedBrokerPosition(instrument, Direction.Buy, 100) };
        Assert.True(AutomaticLiveExecutionService.HasOnlyManagedExposure(instrument, managed,
            [new BrokerPositionSnapshot(instrument, Direction.Buy, 100, 90m),
                new BrokerPositionSnapshot(otherInstrument, Direction.Buy, 50, 70m)]));
        Assert.False(AutomaticLiveExecutionService.HasOnlyManagedExposure(instrument, managed,
            [new BrokerPositionSnapshot(instrument, Direction.Buy, 150, 90m)]));
        Assert.False(AutomaticLiveExecutionService.HasOnlyManagedExposure(instrument, managed,
            [new BrokerPositionSnapshot(instrument, Direction.Buy, 100, 90m),
                new BrokerPositionSnapshot(instrument, Direction.Sell, 25, 90m)]));
    }

    [Fact]
    public void LiveClientReferenceIsStableForTheSamePaperSource()
    {
        var sourceId = Guid.NewGuid();

        var first = AutomaticLiveExecutionService.LiveClientReference(sourceId);
        var retry = AutomaticLiveExecutionService.LiveClientReference(sourceId);

        Assert.Equal(first, retry);
        Assert.NotEqual(first, AutomaticLiveExecutionService.LiveClientReference(Guid.NewGuid()));
        Assert.True(first.Length <= 20);
    }

    [Fact]
    public void DefiniteUnauthorisedPreSubmissionAttemptDoesNotBlockLaterTrades()
    {
        var intent = new LiveExecutionIntent(Guid.NewGuid(), "SENSEX", Guid.NewGuid(), "SENSEX",
            Guid.NewGuid(), Direction.Buy, 100, 100m, 90m, 110m, "LE12345678",
            DateTimeOffset.UtcNow);
        intent.RequireReconciliation("Groww execution API returned 401.");

        Assert.True(AutomaticLiveExecutionService.IsDefinitivePreSubmissionRejection(intent));
    }
}
