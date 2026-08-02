using TradingSystem.Application.Risk;
using TradingSystem.Application.Strategies;

namespace TradingSystem.Application.Execution;

public interface IPaperLifecycleAuditStore
{
    Task PersistSignalAsync(StrategySignal signal, CancellationToken cancellationToken);

    Task PersistRiskDecisionAsync(
        Guid signalId,
        RiskDecisionResult decision,
        CancellationToken cancellationToken);

    Task PersistReportAsync(PaperTradeReport report, CancellationToken cancellationToken);
}
