using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Auditing;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Risk;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class EfPaperLifecycleAuditStore(
    TradingDbContext dbContext,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IPaperLifecycleAuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PersistSignalAsync(
        StrategySignal signal,
        CancellationToken cancellationToken)
    {
        if (await dbContext.Signals.AnyAsync(value => value.Id == signal.SignalId, cancellationToken))
        {
            return;
        }

        var strategy = await dbContext.Strategies.SingleOrDefaultAsync(
            value => value.Code == signal.StrategyId,
            cancellationToken);
        if (strategy is null)
        {
            strategy = new Strategy(Guid.NewGuid(), signal.StrategyId,
                "Opening Range Breakout", timeProvider.GetUtcNow());
            dbContext.Strategies.Add(strategy);
        }

        var version = await dbContext.StrategyVersions.SingleOrDefaultAsync(
            value => value.StrategyId == strategy.Id && value.Version == signal.StrategyVersion,
            cancellationToken);
        if (version is null)
        {
            version = new StrategyVersion(
                Guid.NewGuid(),
                strategy.Id,
                signal.StrategyVersion,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{signal.StrategyId}:{signal.StrategyVersion}"))),
                timeProvider.GetUtcNow());
            dbContext.StrategyVersions.Add(version);
        }

        var reasons = JsonSerializer.Serialize(new
        {
            supporting = signal.SupportingReasons,
            invalidating = signal.InvalidatingReasons,
            entryType = signal.EntryType,
            rewardToRiskRatio = signal.RewardToRiskRatio,
            regime = signal.Regime
        }, JsonOptions);
        dbContext.Signals.Add(new Signal(
            signal.SignalId,
            version.Id,
            signal.InstrumentId,
            signal.Direction,
            signal.ProposedEntry,
            signal.ProposedStopLoss,
            signal.ProposedTarget,
            signal.Confidence,
            signal.MarketDataTimestampUtc,
            signal.ExpiresAtUtc,
            $"{signal.StrategyId}:{signal.StrategyVersion}:{signal.SignalId:N}",
            reasons));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task PersistRiskDecisionAsync(
        Guid signalId,
        RiskDecisionResult decision,
        CancellationToken cancellationToken)
    {
        if (await dbContext.RiskDecisions.AnyAsync(
                value => value.SignalId == signalId,
                cancellationToken))
        {
            return;
        }

        dbContext.RiskDecisions.Add(new RiskDecision(
            Guid.NewGuid(),
            signalId,
            decision.Approved,
            decision.ApprovedQuantity,
            JsonSerializer.Serialize(decision.RejectionReasons, JsonOptions),
            JsonSerializer.Serialize(new
            {
                decision.FinalStopLoss,
                decision.FinalTarget,
                decision.RiskAmount,
                decision.CapitalExposure
            }, JsonOptions),
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task PersistReportAsync(
        PaperTradeReport report,
        CancellationToken cancellationToken)
    {
        var correlationId = report.Signal.SignalId.ToString("N");
        if (await dbContext.AuditLogs.AnyAsync(
                value => value.Action == "PaperTradeClosed" &&
                         value.CorrelationId == correlationId,
                cancellationToken))
        {
            return;
        }

        await auditWriter.WriteAsync(new AuditEntry(
            "paper-lifecycle",
            "PaperTradeClosed",
            "Signal",
            correlationId,
            "Deterministic paper lifecycle completed.",
            "{}",
            JsonSerializer.Serialize(new
            {
                entryOrder = report.EntryOrder,
                exitOrder = report.ExitOrder,
                report.RealisedPnl,
                report.ClosedAtUtc
            }, JsonOptions),
            correlationId,
            report.ClosedAtUtc),
            cancellationToken);
    }
}
