using TradingSystem.Application.Broker;
using TradingSystem.Application.Risk;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record PaperTradeReport(StrategySignal Signal, RiskDecisionResult RiskDecision,
    BrokerOrderSnapshot EntryOrder, BrokerOrderSnapshot ExitOrder, decimal RealisedPnl,
    DateTimeOffset ClosedAtUtc);

public sealed record PaperLifecycleResult(StrategySignal? Signal, RiskDecisionResult? RiskDecision,
    PaperTradeReport? Report, IReadOnlyList<string> Reasons);

public sealed class PaperTradeLifecycleService(ITradingStrategy strategy,
    PreliminaryRiskEngine riskEngine, IBrokerGateway brokerGateway,
    IPaperBrokerControl paperControl, TimeProvider timeProvider)
{
    public async Task<PaperLifecycleResult> RunAsync(StrategyEvaluationContext strategyContext,
        RiskContext riskContext, decimal deterministicExitPrice, int maximumFillPerCycle,
        CancellationToken cancellationToken)
    {
        if (brokerGateway.Mode != TradingMode.Paper)
            throw new InvalidOperationException("Phase 7 lifecycle is restricted to Paper mode.");
        var signal = strategy.Evaluate(strategyContext);
        if (signal is null) return new(null, null, null, ["Strategy produced no signal."]);
        var decision = riskEngine.Evaluate(signal, riskContext);
        if (!decision.Approved) return new(signal, decision, null, decision.RejectionReasons);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deterministicExitPrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFillPerCycle);

        var entryReference = $"{signal.SignalId:N}-ENTRY";
        await brokerGateway.SubmitAsync(new(entryReference, signal.InstrumentId, signal.Direction,
            decision.ApprovedQuantity, signal.ProposedEntry, maximumFillPerCycle), cancellationToken);
        var entry = await FillCompletelyAsync(entryReference, cancellationToken);
        var exitDirection = signal.Direction == Direction.Buy ? Direction.Sell : Direction.Buy;
        var exitReference = $"{signal.SignalId:N}-EXIT";
        await brokerGateway.SubmitAsync(new(exitReference, signal.InstrumentId, exitDirection,
            entry.FilledQuantity, deterministicExitPrice, maximumFillPerCycle), cancellationToken);
        var exit = await FillCompletelyAsync(exitReference, cancellationToken);
        var multiplier = signal.Direction == Direction.Buy ? 1m : -1m;
        var pnl = (exit.AverageFillPrice!.Value - entry.AverageFillPrice!.Value) *
                  entry.FilledQuantity * multiplier;
        var report = new PaperTradeReport(signal, decision, entry, exit, pnl, timeProvider.GetUtcNow());
        return new(signal, decision, report, []);
    }

    private async Task<BrokerOrderSnapshot> FillCompletelyAsync(string clientReference,
        CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot snapshot;
        do
        {
            snapshot = await paperControl.ProcessNextFillAsync(clientReference, cancellationToken);
        } while (snapshot.State == OrderState.PartiallyFilled);
        if (snapshot.State != OrderState.Filled)
            throw new InvalidOperationException($"Paper order ended in unexpected state {snapshot.State}.");
        return snapshot;
    }
}
