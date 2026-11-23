using System.Text.Json;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.ContractTests;

public sealed class LiveExternalExitTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 23, 4, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<string> References = ["LE12345678", "LP12345678"];
    private static LiveExecutionIntent Intent(string market = "NIFTY")
    {
        var i = new LiveExecutionIntent(Guid.NewGuid(), "NIFTY", Guid.NewGuid(), market,
            Guid.NewGuid(), Direction.Buy, 100, 100, 90, 120, "LE12345678", At);
        i.Submitted("entry", At); i.Filled(100, 100); i.Protected("stop", At);
        return i;
    }
    private static LiveAccountSnapshot Snapshot(int external, int held, int stopSize = 100,
        OrderState stopState = OrderState.BrokerAcknowledged) => new([
            new("entry", "LE12345678", "OPT", "NSE", "NRML", Direction.Buy, 100, 100, OrderState.Filled, At),
            new("stop", "LP12345678", "OPT", "NSE", "NRML", Direction.Sell, stopSize, 0, stopState, At, 90),
            new("manual", "", "OPT", "NSE", "NRML", Direction.Sell, external, external, OrderState.Filled, At.AddMinutes(1))
        ], [new("OPT", "NSE", "NRML", held)], At.AddMinutes(2));

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANK-NIFTY")]
    public async Task FullManualExitCancelsOnlyOwnedStopAndIsIdempotent(string market)
    {
        var i = Intent(market); var initial = Snapshot(100, 0);
        var gateway = new FakeGateway(Snapshot(100, 0, stopState: OrderState.Cancelled));
        var saves = 0;
        Assert.True(await Run(i, initial, gateway, () => { saves++; if (saves == 1) Assert.True(i.ExternalReconciliationPending); }));
        Assert.Equal("Closed", i.Status); Assert.Equal(0, i.FilledQuantity);
        Assert.Equal(100, i.ExternalExitQuantity); Assert.Equal(["stop"], gateway.Cancels);
        Assert.Empty(gateway.Modifications); Assert.Equal(2, saves);
        Assert.False(i.ExternalReconciliationPending);
        var evidence = JsonSerializer.Deserialize<ExternalExitFill[]>(i.ExternalExitEvidenceJson!)!;
        Assert.Single(evidence); Assert.Equal(125m, evidence[0].AveragePrice);
        // Durable closed status is excluded from discovery by existing SourceId uniqueness.
        Assert.False(LiveExternalExitReconciler.Assess(i, "OPT", "NSE", gateway.After, 1, References).Changed);
    }

    [Fact]
    public async Task PartialExitResizesStopWithoutChangingPaperScalingCounters()
    {
        var i = Intent(); var gateway = new FakeGateway(Snapshot(40, 60, 60));
        Assert.True(await Run(i, Snapshot(40, 60), gateway));
        Assert.Equal("Protected", i.Status); Assert.Equal(60, i.FilledQuantity);
        Assert.Equal(40, i.ExternalExitQuantity); Assert.Equal(0, i.MirroredPartialExitQuantity);
        Assert.Equal(0, i.MirroredAddOnQuantity); Assert.Equal(90m, i.StopLoss);
        Assert.Single(gateway.Modifications); Assert.Empty(gateway.Cancels);
        Assert.True(await Run(i, gateway.After, gateway));
        Assert.Single(gateway.Modifications); // Duplicate notification does not resize again.
    }

    [Fact]
    public async Task RestartAfterCancellationCompletesWithoutSecondCancel()
    {
        var i = Intent(); i.BeginExternalReconciliation();
        var cancelled = Snapshot(100, 0, stopState: OrderState.Cancelled);
        var gateway = new FakeGateway(cancelled);
        Assert.True(await Run(i, cancelled, gateway));
        Assert.Equal("Closed", i.Status); Assert.Empty(gateway.Cancels);
    }

    [Fact]
    public async Task CancellationTimeoutKeepsDurableHoldAndCanRecover()
    {
        var i = Intent(); var gateway = new FakeGateway(Snapshot(100, 0, stopState: OrderState.Cancelled)) { CancelFails = true };
        await Assert.ThrowsAsync<IOException>(() => Run(i, Snapshot(100, 0), gateway));
        Assert.True(i.ExternalReconciliationPending); Assert.Equal(100, i.FilledQuantity);
        gateway.CancelFails = false;
        Assert.True(await Run(i, gateway.After, gateway)); Assert.Equal("Closed", i.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task SimultaneousStopFillNeverClaimsCleanManualClosure(int held)
    {
        var i = Intent(); var raced = Snapshot(100, held);
        raced = raced with { Orders = raced.Orders.Select(o => o.Id == "stop" ? o with { Filled = 100, State = OrderState.Filled } : o).ToArray() };
        var gateway = new FakeGateway(raced);
        Assert.False(await Run(i, Snapshot(100, 0), gateway));
        Assert.True(i.ExternalReconciliationPending); Assert.NotEqual("Closed", i.Status);
        Assert.Equal(0, i.ExternalExitQuantity); Assert.Equal(100, i.FilledQuantity);
    }

    [Fact]
    public async Task SameContractAmbiguityNeverCancelsOrAttributesAnotherPosition()
    {
        var i = Intent(); var gateway = new FakeGateway(Snapshot(40, 160));
        Assert.False(await LiveExternalExitReconciler.ReconcileAsync(i, "OPT", "NSE", gateway.After, 2,
            References, gateway, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Empty(gateway.Cancels); Assert.Empty(gateway.Modifications); Assert.Equal(0, gateway.FillReads);
        Assert.True(i.ExternalReconciliationPending);
    }

    [Fact]
    public async Task QuantityMismatchAndIncompletePricesDoNotBecomeManualProfits()
    {
        var i = Intent(); var gateway = new FakeGateway(Snapshot(40, 0));
        Assert.False(await Run(i, gateway.After, gateway)); Assert.Empty(gateway.Cancels);
        i = Intent(); gateway = new FakeGateway(Snapshot(100, 0, stopState: OrderState.Cancelled)) { FillFails = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(i, Snapshot(100, 0), gateway));
        Assert.True(i.ExternalReconciliationPending); Assert.Single(gateway.Cancels);
    }

    [Fact]
    public async Task ForeignStopIsNeverModified()
    {
        var initial = Snapshot(40, 60);
        initial = initial with { Orders = initial.Orders.Select(o => o.Id == "stop"
            ? o with { Reference = "someone-else" } : o).ToArray() };
        var gateway = new FakeGateway(initial);
        Assert.False(await Run(Intent(), initial, gateway));
        Assert.Empty(gateway.Cancels); Assert.Empty(gateway.Modifications);
    }

    [Fact]
    public async Task WorkingManualPartialResizesStopButHoldsActionsUntilTerminal()
    {
        var i = Intent();
        LiveAccountSnapshot Working(int stopSize) { var s = Snapshot(40, 60, stopSize); return s with
            { Orders = s.Orders.Select(o => o.Id == "manual" ? o with { Quantity = 100, State = OrderState.PartiallyFilled } : o).ToArray() }; }
        var gateway = new FakeGateway(Working(60));
        Assert.False(await Run(i, Working(100), gateway));
        Assert.Equal([60], gateway.Modifications); Assert.True(i.ExternalReconciliationPending);
        Assert.Equal(100, i.FilledQuantity); Assert.Equal(0, i.ExternalExitQuantity);
        gateway.After = Snapshot(40, 60, 60);
        Assert.True(await Run(i, gateway.After, gateway));
        Assert.Equal(60, i.FilledQuantity); Assert.Equal(40, i.ExternalExitQuantity);
        Assert.Single(gateway.Modifications);
    }

    [Fact]
    public void HealthySharedContractDoesNotBecomeAnExternalExit()
    {
        var s = Snapshot(0, 200); s = s with { Orders = s.Orders.Where(o => o.Id != "manual").ToArray() };
        Assert.False(LiveExternalExitReconciler.Assess(Intent(), "OPT", "NSE", s, 2, References, 200).Changed);
    }

    [Fact]
    public async Task UnfilledManualSellHoldsAutomationAndCancellationRestoresIt()
    {
        var i = Intent(); var s = Snapshot(0, 100);
        s = s with { Orders = s.Orders.Select(o => o.Id == "manual"
            ? o with { Quantity = 100, State = OrderState.BrokerAcknowledged } : o).ToArray() };
        var gateway = new FakeGateway(s);
        Assert.False(await Run(i, s, gateway)); Assert.True(i.ExternalReconciliationPending);
        Assert.Empty(gateway.Modifications); Assert.Empty(gateway.Cancels);
        gateway.After = s with { Orders = s.Orders.Select(o => o.Id == "manual" ? o with { State = OrderState.Cancelled } : o).ToArray() };
        Assert.True(await Run(i, gateway.After, gateway));
        Assert.False(i.ExternalReconciliationPending); Assert.Equal(100, i.FilledQuantity);
        Assert.Equal(0, i.ExternalExitQuantity);
    }

    [Fact]
    public async Task OwnedStopAfterManualPartialStillClosesThroughNormalStopReconciliation()
    {
        var i = Intent(); await Run(i, Snapshot(40, 60), new FakeGateway(Snapshot(40, 60, 60)));
        var s = Snapshot(40, 0, 60, OrderState.Filled);
        s = s with { Orders = s.Orders.Select(o => o.Id == "stop" ? o with { Filled = 60 } : o).ToArray() };
        Assert.False(LiveExternalExitReconciler.Assess(i, "OPT", "NSE", s, 1, References).Changed);
        i.Closed(At.AddMinutes(5));
        var pnl = LiveFillPnl.Calculate(i, [new(i.SourceId, LiveFillPnl.Leg.Entry, "entry", 100, 100, 100),
            new(i.SourceId, LiveFillPnl.Leg.Final, "stop", 60, 110, 60)], new Dictionary<LiveFillPnl.Leg, LiveFillPnl.PaperLeg>());
        Assert.Equal("Closed", pnl.Status); Assert.Equal(1600m, pnl.LiveGross);
        Assert.Null(pnl.MatchedPaperGross);
    }

    [Fact]
    public async Task VerifiedPartialStopIsRecordedWithConfirmedQuantity()
    {
        var i = Intent(); BrokerProtectionSnapshot? receipt = null;
        Assert.True(await LiveExternalExitReconciler.ReconcileAsync(i, "OPT", "NSE", Snapshot(40, 60), 1,
            References, new FakeGateway(Snapshot(40, 60, 60)), _ => Task.CompletedTask, CancellationToken.None,
            recordConfirmation: (r, _) => { receipt = r; return Task.CompletedTask; }));
        Assert.Equal(60, receipt!.Quantity); Assert.Equal(90m, receipt.StopLoss);
    }

    [Fact]
    public async Task TwoManualExitOrdersKeepBothFillsWithoutDuplicatingPriorPartial()
    {
        var i = Intent(); await Run(i, Snapshot(40,60),new FakeGateway(Snapshot(40,60,60)));
        var s = Snapshot(40,0,60,OrderState.Cancelled);
        s = s with { Orders = s.Orders.Concat([new LiveAccountOrder("manual2","","OPT","NSE","NRML",
            Direction.Sell,60,60,OrderState.Filled,At.AddMinutes(2))]).ToArray() };
        Assert.True(await Run(i,s,new FakeGateway(s)));
        Assert.Equal("Closed",i.Status); Assert.Equal(100,i.ExternalExitQuantity);
        var pnl=LiveFillPnl.Calculate(i,[new(i.SourceId,LiveFillPnl.Leg.Entry,"entry",100,100,100)],
            new Dictionary<LiveFillPnl.Leg,LiveFillPnl.PaperLeg>());
        Assert.Equal(2500m,pnl.LiveGross); Assert.Equal("Closed",pnl.Status);
    }

    [Fact]
    public async Task ManualExitCanCompleteBeforeAnyPendingSystemFinalSellWasSent()
    {
        var i=Intent(); i.BeginFinalExit();
        Assert.True(await Run(i,Snapshot(100,0),new FakeGateway(Snapshot(100,0,stopState:OrderState.Cancelled))));
        Assert.Equal("Closed",i.Status); Assert.Equal(100,i.ExternalExitQuantity);
    }

    [Fact]
    public async Task InFlightSystemSellAndFailedDurableHoldNeverMutateProtection()
    {
        var i=Intent(); var s=Snapshot(100,0);
        s=s with { Orders=s.Orders.Concat([new LiveAccountOrder("exit","LX-owned","OPT","NSE","NRML",
            Direction.Sell,100,0,OrderState.BrokerAcknowledged,At)]).ToArray() };
        var refs=References.Append("LX-owned").ToHashSet(); var gateway=new FakeGateway(s);
        Assert.False(await LiveExternalExitReconciler.ReconcileAsync(i,"OPT","NSE",s,1,refs,gateway,
            _=>Task.CompletedTask,CancellationToken.None));
        Assert.Empty(gateway.Cancels); Assert.Empty(gateway.Modifications);
        gateway=new FakeGateway(Snapshot(100,0,stopState:OrderState.Cancelled));
        await Assert.ThrowsAsync<IOException>(()=>LiveExternalExitReconciler.ReconcileAsync(Intent(),"OPT","NSE",
            Snapshot(100,0),1,References,gateway,_=>throw new IOException("Save failed"),CancellationToken.None));
        Assert.Empty(gateway.Cancels); Assert.Empty(gateway.Modifications);
    }

    [Fact]
    public async Task ExternalPnlIsActualFillBasedAndNeverComparedAsStrategyExecution()
    {
        var i = Intent(); await Run(i, Snapshot(100, 0), new FakeGateway(Snapshot(100, 0, stopState: OrderState.Cancelled)));
        var entry = new LiveFillPnl.Observation(i.SourceId, LiveFillPnl.Leg.Entry, "entry", 100, 100, 100);
        var pnl = LiveFillPnl.Calculate(i, [entry], new Dictionary<LiveFillPnl.Leg, LiveFillPnl.PaperLeg>
            { [LiveFillPnl.Leg.Entry] = new(100, 100), [LiveFillPnl.Leg.Final] = new(100, 125) });
        Assert.Equal("Closed", pnl.Status); Assert.Equal(2500m, pnl.LiveGross);
        Assert.Null(pnl.MatchedPaperGross); Assert.Null(pnl.ExecutionDifference);
        Assert.True(pnl.EstimatedNet < pnl.LiveGross);
    }

    [Fact]
    public void NotificationSubjectsAreAccountOnlyAndRejectWildcardInjection()
    {
        Assert.Equal(["stocks_fo/order/updates.apex.account-123", "stocks_fo/position/updates.apex.account-123"],
            GrowwLiveAccountNotifications.Subjects("account-123"));
        Assert.Throws<InvalidDataException>(() => GrowwLiveAccountNotifications.Subjects("*"));
    }

    private static Task<bool> Run(LiveExecutionIntent i, LiveAccountSnapshot snapshot, FakeGateway gateway, Action? saved = null) =>
        LiveExternalExitReconciler.ReconcileAsync(i, "OPT", "NSE", snapshot, 1, References, gateway,
            _ => { saved?.Invoke(); return Task.CompletedTask; }, CancellationToken.None);

    private sealed class FakeGateway(LiveAccountSnapshot after) : ILiveAccountEvidenceGateway
    {
        public LiveAccountSnapshot After = after;
        public List<string> Cancels = [];
        public List<int> Modifications = [];
        public int FillReads;
        public bool CancelFails, FillFails;
        public Task<LiveAccountSnapshot> ReadAccountAsync(CancellationToken token) => Task.FromResult(After);
        public Task<ExternalExitFill> ReadExternalFillAsync(string id, int qty, CancellationToken token)
        { FillReads++; if (FillFails) throw new InvalidDataException(); return Task.FromResult(new ExternalExitFill(id, qty, 125m, At)); }
        public Task CancelProtectionAsync(string id, CancellationToken token)
        { Cancels.Add(id); if (CancelFails) throw new IOException(); return Task.CompletedTask; }
        public Task<BrokerProtectionSnapshot> ModifyProtectionAsync(string id, int qty, decimal stop, CancellationToken token)
        { Modifications.Add(qty); return Task.FromResult(new BrokerProtectionSnapshot("LP12345678", id, "OPEN", qty, stop, At)); }
    }
}
