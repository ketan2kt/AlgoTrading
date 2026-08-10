using TradingSystem.Application.Risk;
using TradingSystem.Application.Strategies;

namespace TradingSystem.Application.Execution;

public interface IPaperLifecycleAuditStore
{
    Task PersistSignalAsync(StrategySignal signal, CancellationToken cancellationToken);

    Task PersistRiskDecisionAsync(
        Guid signalId,
        RiskDecisionResult decision,
        CancellationToken cancellationToken,
        PaperOptionExecutionProposal? optionProposal = null);

    Task PersistReportAsync(PaperTradeReport report, CancellationToken cancellationToken);
}

public sealed record PaperOptionExecutionProposal(
    Guid InstrumentId,
    string TradingSymbol,
    string InstrumentType,
    DateOnly ExpiryDate,
    decimal StrikePrice,
    int LotSize,
    int MaximumLots,
    decimal ProposedEntry,
    decimal ProposedStopLoss,
    decimal ProposedTarget);
