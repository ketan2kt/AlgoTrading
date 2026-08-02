using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Risk;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure;

namespace TradingSystem.UnitTests;

public sealed class OpeningRangeBreakoutStrategyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 4, 30, 0, TimeSpan.Zero);
    private readonly OpeningRangeBreakoutStrategy strategy = new(new());

    [Fact]
    public void BullishBreakoutCreatesImmutableRiskDefinedSignal()
    {
        var signal = strategy.Evaluate(Context());

        Assert.NotNull(signal);
        Assert.Equal(Direction.Buy, signal.Direction);
        Assert.Equal(2m, signal.RewardToRiskRatio);
        Assert.True(signal.ProposedStopLoss < signal.ProposedEntry);
        Assert.True(signal.ProposedTarget > signal.ProposedEntry);
        Assert.Equal("opening-range-breakout", signal.StrategyId);
    }

    [Theory]
    [InlineData(false, true, 1.5, 0)]
    [InlineData(true, false, 1.5, 0)]
    [InlineData(true, true, 1.0, 0)]
    [InlineData(true, true, 1.5, 1)]
    public void SafetyGatesProduceNoSignal(bool regimePermitted, bool dataPermitted,
        decimal volume, int trades)
    {
        Assert.Null(strategy.Evaluate(Context() with
        {
            RegimeTradingPermitted = regimePermitted,
            DataTradingPermitted = dataPermitted,
            RelativeVolume = volume,
            TradesToday = trades
        }));
    }

    [Fact]
    public void PreliminaryRiskSizesByRiskAndRejectsExpiredOrKillSwitch()
    {
        var signal = strategy.Evaluate(Context())!;
        var engine = new PreliminaryRiskEngine(new() { MaximumRiskPerTrade = 100m, MaximumQuantity = 75 });
        var approved = engine.Evaluate(signal, RiskContext());
        var rejected = engine.Evaluate(signal, RiskContext() with { KillSwitchActive = true });

        Assert.True(approved.Approved);
        Assert.InRange(approved.RiskAmount, 0.01m, 100m);
        Assert.False(rejected.Approved);
        Assert.Equal(0, rejected.ApprovedQuantity);
    }

    [Fact]
    public async Task CompletePaperLifecycleEntersPartiallyFillsExitsAndReports()
    {
        await using var provider = CreateProvider();
        var audit = new TestPaperLifecycleAuditStore();
        var lifecycle = new PaperTradeLifecycleService(strategy,
            new PreliminaryRiskEngine(new() { MaximumRiskPerTrade = 500m, MaximumQuantity = 10 }),
            provider.GetRequiredService<IBrokerGateway>(),
            provider.GetRequiredService<IPaperBrokerControl>(), audit, new FixedTimeProvider(Now));

        var result = await lifecycle.RunAsync(Context(), RiskContext(),
            deterministicExitPrice: 103m, maximumFillPerCycle: 3, CancellationToken.None);

        Assert.NotNull(result.Report);
        Assert.Equal(OrderState.Filled, result.Report.EntryOrder.State);
        Assert.Equal(OrderState.Filled, result.Report.ExitOrder.State);
        Assert.True(result.Report.RealisedPnl > 0);
        Assert.Equal(1, audit.SignalWrites);
        Assert.Equal(1, audit.DecisionWrites);
        Assert.Equal(1, audit.ReportWrites);
        Assert.Empty(await provider.GetRequiredService<IBrokerGateway>()
            .GetPositionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RiskRejectionNeverSubmitsPaperOrder()
    {
        await using var provider = CreateProvider();
        var gateway = provider.GetRequiredService<IBrokerGateway>();
        var audit = new TestPaperLifecycleAuditStore();
        var lifecycle = new PaperTradeLifecycleService(strategy, new PreliminaryRiskEngine(new()),
            gateway, provider.GetRequiredService<IPaperBrokerControl>(), audit, new FixedTimeProvider(Now));

        var result = await lifecycle.RunAsync(Context(), RiskContext() with { KillSwitchActive = true },
            103m, 3, CancellationToken.None);

        Assert.Null(result.Report);
        Assert.Contains(result.Reasons, reason => reason.Contains("Kill switch", StringComparison.Ordinal));
        Assert.Equal(1, audit.SignalWrites);
        Assert.Equal(1, audit.DecisionWrites);
        Assert.Equal(0, audit.ReportWrites);
        Assert.Null(await gateway.GetOrderAsync($"{result.Signal!.SignalId:N}-ENTRY", CancellationToken.None));
    }

    private static StrategyEvaluationContext Context() => new(Guid.NewGuid(), Now, 102m,
        101m, 99m, 1.5m, MarketRegime.StrongBullishTrend, Direction.Buy, 0.80m,
        true, true, null, 0);

    private static RiskContext RiskContext() => new(Now, 0, 0, 0m, 1000m,
        false, true, true);

    private static ServiceProvider CreateProvider()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TradingDatabase"] = "Host=localhost;Database=unused;Username=unused",
            ["Trading:Mode"] = "Paper",
            ["IdentityBootstrap:Enabled"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPaperBrokerJournal>(new TestPaperBrokerJournal());
        services.AddTradingSystemInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TestPaperLifecycleAuditStore : IPaperLifecycleAuditStore
    {
        public int SignalWrites { get; private set; }
        public int DecisionWrites { get; private set; }
        public int ReportWrites { get; private set; }

        public Task PersistSignalAsync(StrategySignal signal, CancellationToken cancellationToken)
        {
            SignalWrites++;
            return Task.CompletedTask;
        }

        public Task PersistRiskDecisionAsync(
            Guid signalId,
            RiskDecisionResult decision,
            CancellationToken cancellationToken)
        {
            DecisionWrites++;
            return Task.CompletedTask;
        }

        public Task PersistReportAsync(PaperTradeReport report, CancellationToken cancellationToken)
        {
            ReportWrites++;
            return Task.CompletedTask;
        }
    }
}
