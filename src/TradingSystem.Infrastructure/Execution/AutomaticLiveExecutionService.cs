using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Risk;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Broker.Groww;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed partial class AutomaticLiveExecutionService(
    IServiceScopeFactory scopeFactory,
    GrowwBrokerGateway broker,
    IOptions<LiveExecutionOptions> options,
    TimeProvider timeProvider,
    ILogger<AutomaticLiveExecutionService> logger) : BackgroundService
{
    internal const string ControlledTestKey = "LiveControlledBrokerTestCompleted";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BuildEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { LogCycleFailed(logger, exception); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var arm = await scope.ServiceProvider.GetRequiredService<ILiveTradingArmService>()
            .GetAsync(cancellationToken);
        if (!arm.Armed || arm.ChangedAtUtc is null) return;
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        if (await db.LiveExecutionIntents.AnyAsync(value =>
                value.Status == "ReconciliationRequired" || value.Status == "ProtectionSubmitting",
                cancellationToken))
            return;
        if (!await ReconcileAsync(db, cancellationToken)) return;

        var recoverable = await db.LiveExecutionIntents.FirstOrDefaultAsync(value =>
            value.Status == "Pending" || value.Status == "Submitted" ||
            value.Status == "PartiallyFilled" || value.Status == "FilledUnprotected",
            cancellationToken);
        if (recoverable is not null)
        {
            await SubmitAndProtectAsync(db, recoverable, cancellationToken);
            return;
        }

        var intent = await DiscoverNiftyAsync(db, arm.ChangedAtUtc.Value, cancellationToken)
                     ?? await DiscoverSensexAsync(db, arm.ChangedAtUtc.Value, cancellationToken);
        if (intent is null) return;
        db.LiveExecutionIntents.Add(intent);
        await db.SaveChangesAsync(cancellationToken); // durable idempotency boundary before broker I/O
        await SubmitAndProtectAsync(db, intent, cancellationToken);
    }

    private async Task<bool> ReconcileAsync(TradingDbContext db, CancellationToken cancellationToken)
    {
        var active = await db.LiveExecutionIntents.Where(value => value.Status == "Protected")
            .ToListAsync(cancellationToken);
        var actual = await broker.GetPositionsAsync(cancellationToken);
        foreach (var intent in active)
        {
            var observed = actual.Where(value => value.InstrumentId == intent.InstrumentId &&
                    value.Direction == intent.Direction).Sum(value => value.Quantity);
            if (observed == 0) intent.Closed(timeProvider.GetUtcNow());
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
        active = active.Where(value => value.Status == "Protected").ToList();
        var expected = active.Select(value => new ExpectedBrokerPosition(value.InstrumentId,
            value.Direction, value.FilledQuantity)).ToArray();
        var result = await broker.ReconcileAsync(expected, cancellationToken);
        if (result.TradingPermitted) return true;
        LogReconciliationBlocked(logger, string.Join(" ", result.Mismatches));
        return false;
    }

    private async Task<LiveExecutionIntent?> DiscoverNiftyAsync(TradingDbContext db,
        DateTimeOffset armedAtUtc, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddSeconds(-options.Value.SignalMaximumAgeSeconds);
        var evaluations = await db.StrategyEvaluations.AsNoTracking()
            .Where(value => value.Outcome == "PaperPositionOpened" && value.SignalId != null &&
                            value.RecordedAtUtc >= armedAtUtc && value.RecordedAtUtc >= cutoff)
            .OrderBy(value => value.RecordedAtUtc).ToListAsync(cancellationToken);
        foreach (var evaluation in evaluations)
        {
            if (await db.LiveExecutionIntents.AnyAsync(value => value.SourceType == "NIFTY" &&
                    value.SourceId == evaluation.Id, cancellationToken)) continue;
            var risk = await db.RiskDecisions.AsNoTracking().SingleOrDefaultAsync(value =>
                value.SignalId == evaluation.SignalId!.Value && value.Approved, cancellationToken);
            if (risk is null || !TryReadOptionProposal(risk.SnapshotJson, out var proposal)) continue;
            var instrument = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(value =>
                value.Id == proposal.InstrumentId && value.IsActive, cancellationToken);
            if (instrument is null || instrument.Exchange != "NSE") continue;
            var quantity = await QuantityAsync(db, instrument.LotSize, cancellationToken);
            return NewIntent("NIFTY", evaluation.Id, instrument, quantity, proposal.EntryPrice,
                proposal.StopLoss, proposal.Target);
        }
        return null;
    }

    private async Task<LiveExecutionIntent?> DiscoverSensexAsync(TradingDbContext db,
        DateTimeOffset armedAtUtc, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddSeconds(-options.Value.SignalMaximumAgeSeconds);
        var sources = await db.MarketPaperPositions.AsNoTracking()
            .Where(value => value.Market == TradingMarketCatalog.Sensex.Code && value.Status == "Active" &&
                            value.OpenedAtUtc >= armedAtUtc && value.OpenedAtUtc >= cutoff)
            .OrderBy(value => value.OpenedAtUtc).ToListAsync(cancellationToken);
        foreach (var source in sources)
        {
            if (await db.LiveExecutionIntents.AnyAsync(value => value.SourceType == "SENSEX" &&
                    value.SourceId == source.Id, cancellationToken)) continue;
            var instrument = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(value =>
                value.Id == source.ExecutionInstrumentId && value.IsActive, cancellationToken);
            if (instrument is null || instrument.Exchange != "BSE") continue;
            var quantity = await QuantityAsync(db, instrument.LotSize, cancellationToken);
            return NewIntent("SENSEX", source.Id, instrument, quantity, source.EntryPrice,
                source.StopLoss, source.Target);
        }
        return null;
    }

    private LiveExecutionIntent NewIntent(string market, Guid sourceId, Instrument instrument,
        int quantity, decimal entry, decimal stop, decimal target)
    {
        var id = Guid.NewGuid();
        return new LiveExecutionIntent(id, market, sourceId, market, instrument.Id, Direction.Buy,
            quantity, entry, stop, target, Reference("LE", id), timeProvider.GetUtcNow());
    }

    private async Task<int> QuantityAsync(TradingDbContext db, int lotSize,
        CancellationToken cancellationToken)
    {
        var completed = await db.ApplicationSettings.AsNoTracking().AnyAsync(value =>
            value.Mode == TradingMode.Live && value.Key == ControlledTestKey &&
            value.ValueJson == "true", cancellationToken);
        var lots = completed ? options.Value.MaximumLotsPerOrder : options.Value.ControlledTestLotsPerOrder;
        return checked(lotSize * lots);
    }

    private async Task SubmitAndProtectAsync(TradingDbContext db, LiveExecutionIntent intent,
        CancellationToken cancellationToken)
    {
        var instrument = await db.Instruments.AsNoTracking().SingleAsync(value =>
            value.Id == intent.InstrumentId, cancellationToken);
        try
        {
            var order = await broker.SubmitAsync(new BrokerOrderRequest(intent.ClientReference,
                instrument.Id, intent.Direction, intent.Quantity, intent.RequestedEntry,
                TradingSymbol: instrument.TradingSymbol, Exchange: instrument.Exchange,
                Segment: "FNO", Product: "NRML", OrderType: "MARKET"), cancellationToken);
            intent.Submitted(order.BrokerOrderId, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            for (var attempt = 0; attempt < 20 && order.FilledQuantity == 0 && order.State is not
                     (OrderState.Cancelled or OrderState.RejectedByBroker or
                      OrderState.ReconciliationRequired); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                order = await broker.GetOrderAsync(intent.ClientReference, cancellationToken)
                        ?? throw new BrokerOrderOutcomeUnknownException(intent.ClientReference);
            }
            if (order.FilledQuantity <= 0 || order.AverageFillPrice is null)
                throw new BrokerOrderOutcomeUnknownException(intent.ClientReference);
            if (order.FilledQuantity < order.RequestedQuantity)
            {
                var cancelled = await broker.CancelAsync(intent.ClientReference, cancellationToken);
                if (cancelled.State != OrderState.Cancelled)
                    throw new BrokerOrderOutcomeUnknownException(intent.ClientReference);
                order = await broker.GetOrderAsync(intent.ClientReference, cancellationToken) ?? cancelled;
                if (order.State != OrderState.Cancelled || order.FilledQuantity <= 0 ||
                    order.AverageFillPrice is null)
                    throw new BrokerOrderOutcomeUnknownException(intent.ClientReference);
            }
            intent.Filled(order.FilledQuantity, order.AverageFillPrice.Value);
            await db.SaveChangesAsync(cancellationToken);
            var stopDistance = Math.Abs(intent.RequestedEntry - intent.StopLoss);
            var targetDistance = Math.Abs(intent.Target - intent.RequestedEntry);
            var protectedStop = order.AverageFillPrice.Value - stopDistance;
            var protectedTarget = order.AverageFillPrice.Value + targetDistance;
            if (protectedStop <= 0 || protectedTarget <= order.AverageFillPrice.Value)
                throw new InvalidOperationException("Filled price cannot produce valid OCO protection geometry.");
            intent.BeginProtection();
            await db.SaveChangesAsync(cancellationToken); // crash here blocks duplicate OCO creation
            var protection = await broker.CreateProtectionAsync(new BrokerProtectionRequest(
                Reference("LP", intent.Id), instrument.TradingSymbol, instrument.Exchange,
                order.FilledQuantity, order.FilledQuantity, protectedTarget, protectedStop),
                cancellationToken);
            intent.Protected(protection.BrokerProtectionId, timeProvider.GetUtcNow());
            await MarkControlledTestCompleteAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            intent.RequireReconciliation(exception.Message);
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task MarkControlledTestCompleteAsync(TradingDbContext db,
        CancellationToken cancellationToken)
    {
        if (await db.ApplicationSettings.AnyAsync(value => value.Mode == TradingMode.Live &&
                value.Key == ControlledTestKey, cancellationToken)) return;
        db.ApplicationSettings.Add(new ApplicationSetting(Guid.NewGuid(), TradingMode.Live,
            ControlledTestKey, "true", timeProvider.GetUtcNow()));
    }

    private static string Reference(string prefix, Guid id) => prefix + id.ToString("N")[..16];

    private static bool TryReadOptionProposal(string json, out OptionProposal proposal)
    {
        proposal = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("optionProposal", out var node) ||
                node.ValueKind != JsonValueKind.Object) return false;
            proposal = new(node.GetProperty("instrumentId").GetGuid(),
                node.GetProperty("entryPrice").GetDecimal(),
                node.GetProperty("stopLoss").GetDecimal(), node.GetProperty("target").GetDecimal());
            return proposal.InstrumentId != Guid.Empty && proposal.EntryPrice > 0 &&
                   proposal.StopLoss > 0 && proposal.Target > 0;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private readonly record struct OptionProposal(Guid InstrumentId, decimal EntryPrice,
        decimal StopLoss, decimal Target);

    [LoggerMessage(LogLevel.Error, "Automatic live execution cycle failed closed.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Critical, "Live broker reconciliation blocked entries: {mismatches}")]
    private static partial void LogReconciliationBlocked(ILogger logger, string mismatches);
}
