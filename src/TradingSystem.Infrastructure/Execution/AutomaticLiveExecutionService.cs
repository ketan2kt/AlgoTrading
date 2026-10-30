using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
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
    LiveExecutionWakeSignal wakeSignal,
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
            await wakeSignal.WaitAsync(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds),
                stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var armService = scope.ServiceProvider.GetRequiredService<ILiveTradingArmService>();
        var arm = await armService.GetAsync(cancellationToken);
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        // Disarming prevents new entries, not the management of positions already
        // opened while armed. Paper exits and protection changes still have to flow.
        await SyncProtectedPositionsAsync(db, cancellationToken);
        await RecoverSubmittedEntryAsync(db, cancellationToken);
        await RecoverSubmittedProtectionAsync(db, cancellationToken);
        foreach (var marketArm in arm.Markets.Where(value => value.Armed && value.ChangedAtUtc is not null))
            await AuditMissedClosedPositionsAsync(db, marketArm.Market.ToLowerInvariant(),
                marketArm.ChangedAtUtc!.Value, cancellationToken);
        if (!await ReconcileAsync(db, cancellationToken)) return;

        var blockers = await db.LiveExecutionIntents.Where(value =>
                value.Status == "ReconciliationRequired" || value.Status == "EntrySubmitting" ||
                value.Status == "ProtectionSubmitting" ||
                (value.Status == "Protected" && value.LastError != null))
            .ToListAsync(cancellationToken);
        foreach (var stale in blockers.Where(IsDefinitivePreSubmissionRejection))
            stale.Reject(stale.LastError!, timeProvider.GetUtcNow());
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
        if (blockers.Any(value => value.Status is "ReconciliationRequired" or "EntrySubmitting" or "ProtectionSubmitting" ||
                                  value.Status == "Protected" && value.LastError is not null))
            return;
        var inFlight = await db.LiveExecutionIntents.FirstOrDefaultAsync(value =>
            value.Status == "Submitted" || value.Status == "PartiallyFilled" ||
            value.Status == "FilledUnprotected", cancellationToken);
        if (inFlight is not null)
        {
            await SubmitAndProtectAsync(db, inFlight, cancellationToken);
            return;
        }
        var armedMarkets = arm.Markets.Where(value => value.Armed && value.ChangedAtUtc is not null)
            .Select(value => value.Market).ToArray();
        if (armedMarkets.Length == 0) return;
        var pending = await db.LiveExecutionIntents.Where(value =>
            value.Status == "Pending" && armedMarkets.Contains(value.Market))
            .OrderBy(value => value.CreatedAtUtc).ToListAsync(cancellationToken);
        var recoverable = pending.FirstOrDefault(value =>
            arm.ForMarket(value.Market)?.ChangedAtUtc <= value.CreatedAtUtc);
        if (recoverable is not null)
        {
            var current = await armService.GetAsync(cancellationToken);
            if (current.ForMarket(recoverable.Market)?.Armed == true)
                await SubmitAndProtectAsync(db, recoverable, cancellationToken);
            return;
        }

        // A Hero Zero pair is persisted together. Process multiple eligible paper
        // legs in the same cycle rather than imposing a polling interval per leg.
        for (var entryIndex = 0; entryIndex < 8; entryIndex++)
        {
            LiveExecutionIntent? intent = null;
            foreach (var marketArm in arm.Markets.Where(value => value.Armed && value.ChangedAtUtc is not null))
            {
                intent = await DiscoverMarketPositionAsync(db, marketArm.Market.ToLowerInvariant(),
                    marketArm.ChangedAtUtc!.Value, cancellationToken);
                if (intent is null && marketArm.Market == "NIFTY")
                    intent = await DiscoverNiftyAsync(db, marketArm.ChangedAtUtc.Value, cancellationToken);
                if (intent is not null) break;
            }
            if (intent is null) return;
            db.LiveExecutionIntents.Add(intent);
            await db.SaveChangesAsync(cancellationToken); // durable idempotency boundary before broker I/O
            var current = await armService.GetAsync(cancellationToken);
            if (current.ForMarket(intent.Market)?.Armed != true) return;
            try
            {
                await SubmitAndProtectAsync(db, intent, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await QuarantineUncertainPairAsync(db, intent, cancellationToken);
                throw;
            }
            if (intent.Status == "Rejected")
            {
                await CompensateRejectedPairLegAsync(db, intent, cancellationToken);
                return;
            }
            if (intent.LastError is not null)
            {
                await CompensatePartialPairLegAsync(db, intent, cancellationToken);
                return;
            }
            if (intent.Status != "Protected" || !await ReconcileAsync(db, cancellationToken)) return;
        }
    }

    private async Task AuditMissedClosedPositionsAsync(TradingDbContext db,
        string market, DateTimeOffset armedAtUtc, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddSeconds(-options.Value.SignalMaximumAgeSeconds);
        var closed = await db.MarketPaperPositions.AsNoTracking().Where(value =>
                value.Market == market &&
                value.Status != "Active" && value.OpenedAtUtc >= armedAtUtc &&
                value.ClosedAtUtc >= cutoff)
            .OrderByDescending(value => value.ClosedAtUtc).Take(50)
            .ToListAsync(cancellationToken);
        foreach (var source in closed)
        {
            if (!IsLiveBrokerProtectable(source.Strategy, source.StopLoss)) continue;
            if (await db.LiveExecutionIntents.AsNoTracking().AnyAsync(value =>
                    value.SourceType == "MARKET_POSITION" && value.SourceId == source.Id,
                    cancellationToken)) continue;
            var outcome = $"LiveParityMissed:{source.Id:N}";
            if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                    value.Market == source.Market && value.Outcome == outcome,
                    cancellationToken)) continue;
            db.MarketStrategyAudits.Add(new MarketStrategyAudit(Guid.NewGuid(), source.Market,
                source.UnderlyingInstrumentId, timeProvider.GetUtcNow(), outcome, 0m,
                JsonSerializer.Serialize(new
                {
                    sourcePositionId = source.Id, source.OpenedAtUtc, source.ClosedAtUtc,
                    reason = "Paper position closed before live discovery; no stale live entry was submitted."
                })));
        }
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CompensatePartialPairLegAsync(TradingDbContext db,
        LiveExecutionIntent intent, CancellationToken cancellationToken)
    {
        if (intent.SourceType != "MARKET_POSITION" || intent.Status != "Protected") return;
        var source = await db.MarketPaperPositions.AsNoTracking().SingleAsync(value =>
            value.Id == intent.SourceId, cancellationToken);
        if (!TryOtherPairedLeg(source.Strategy, out _)) return;
        try
        {
            await MirrorFinalExitAsync(db, intent, source.CurrentPrice, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            intent.RequireReconciliation("A partially filled paired leg could not be flattened safely.");
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private static async Task QuarantineUncertainPairAsync(TradingDbContext db,
        LiveExecutionIntent uncertain, CancellationToken cancellationToken)
    {
        if (uncertain.SourceType != "MARKET_POSITION") return;
        var source = await db.MarketPaperPositions.AsNoTracking().SingleAsync(value =>
            value.Id == uncertain.SourceId, cancellationToken);
        if (!TryOtherPairedLeg(source.Strategy, out var otherStrategy)) return;
        var otherId = await db.MarketPaperPositions.AsNoTracking().Where(value =>
                value.Market == source.Market && value.Strategy == otherStrategy &&
                value.OpenedAtUtc == source.OpenedAtUtc)
            .Select(value => value.Id).FirstOrDefaultAsync(cancellationToken);
        if (otherId == Guid.Empty) return;
        var openLeg = await db.LiveExecutionIntents.SingleOrDefaultAsync(value =>
            value.SourceType == "MARKET_POSITION" && value.SourceId == otherId &&
            value.Status == "Protected", cancellationToken);
        if (openLeg is null) return;
        openLeg.RequireReconciliation("Paired counterpart has an uncertain broker outcome; reconcile both legs.");
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CompensateRejectedPairLegAsync(TradingDbContext db,
        LiveExecutionIntent rejected, CancellationToken cancellationToken)
    {
        if (rejected.SourceType != "MARKET_POSITION") return;
        var source = await db.MarketPaperPositions.AsNoTracking().SingleAsync(value =>
            value.Id == rejected.SourceId, cancellationToken);
        if (!TryOtherPairedLeg(source.Strategy, out var otherStrategy)) return;
        var other = await db.MarketPaperPositions.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Market == source.Market && value.Strategy == otherStrategy &&
            value.OpenedAtUtc == source.OpenedAtUtc, cancellationToken);
        if (other is null) return;
        var openLeg = await db.LiveExecutionIntents.SingleOrDefaultAsync(value =>
            value.SourceType == "MARKET_POSITION" && value.SourceId == other.Id &&
            value.Status == "Protected", cancellationToken);
        if (openLeg is null) return;
        try
        {
            await MirrorFinalExitAsync(db, openLeg, other.CurrentPrice, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            openLeg.RequireReconciliation("One paired leg was rejected; the other live leg needs broker reconciliation.");
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task SyncProtectedPositionsAsync(TradingDbContext db,
        CancellationToken cancellationToken)
    {
        var managed = await db.LiveExecutionIntents.Where(value => value.Status == "Protected")
            .OrderBy(value => value.CreatedAtUtc).ToListAsync(cancellationToken);
        foreach (var intent in managed)
        {
            try
            {
                if (intent.SourceType == "MARKET_POSITION")
                    await SyncMarketPositionAsync(db, intent, cancellationToken);
                else if (intent.SourceType == "NIFTY")
                    await SyncNiftyPositionAsync(db, intent, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Do not retry an order with an uncertain broker outcome or open a new
                // position while the existing one needs operator reconciliation.
                intent.RequireReconciliation("Paper/live lifecycle update needs broker reconciliation.");
                await db.SaveChangesAsync(cancellationToken);
                await TryRecordLiveEventAsync(db, intent, "ReconciliationRequired",
                    new { errorType = exception.GetType().Name }, cancellationToken);
                LogCycleFailed(logger, exception);
            }
        }
    }

    private async Task RecoverSubmittedProtectionAsync(TradingDbContext db,
        CancellationToken cancellationToken)
    {
        var pending = await db.LiveExecutionIntents.Where(value =>
                value.Status == "ProtectionSubmitting")
            .ToListAsync(cancellationToken);
        foreach (var intent in pending)
        {
            try
            {
                var reference = Reference("LP", intent.Id);
                BrokerOrderSnapshot? stop = null;
                for (var attempt = 0; attempt < 10 && stop is null; attempt++)
                {
                    stop = await broker.GetOrderAsync(reference, cancellationToken);
                    if (stop is null)
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
                if (stop is null)
                    throw new BrokerOrderOutcomeUnknownException(reference);
                if (stop.State is not (OrderState.BrokerAcknowledged or OrderState.PartiallyFilled))
                    throw new InvalidOperationException("Broker stop is not open during restart recovery.");
                intent.Protected(stop.BrokerOrderId, timeProvider.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                intent.RequireReconciliation("Broker protection needs operator reconciliation after restart.");
                await db.SaveChangesAsync(cancellationToken);
                LogCycleFailed(logger, exception);
            }
        }
    }

    private async Task RecoverSubmittedEntryAsync(TradingDbContext db,
        CancellationToken cancellationToken)
    {
        var pending = await db.LiveExecutionIntents.Where(value =>
                value.Status == "EntrySubmitting")
            .ToListAsync(cancellationToken);
        foreach (var intent in pending)
        {
            try
            {
                BrokerOrderSnapshot? order = null;
                for (var attempt = 0; attempt < 10 && order is null; attempt++)
                {
                    order = await broker.GetOrderAsync(intent.ClientReference, cancellationToken);
                    if (order is null)
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
                if (order is null)
                    throw new BrokerOrderOutcomeUnknownException(intent.ClientReference);
                intent.Submitted(order.BrokerOrderId, timeProvider.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                intent.RequireReconciliation("Broker entry submission outcome is unknown after restart.");
                await db.SaveChangesAsync(cancellationToken);
                LogCycleFailed(logger, exception);
            }
        }
    }

    private async Task SyncMarketPositionAsync(TradingDbContext db,
        LiveExecutionIntent intent, CancellationToken cancellationToken)
    {
        var source = await db.MarketPaperPositions.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Id == intent.SourceId, cancellationToken);
        if (source is null)
            throw new InvalidOperationException("The linked paper position is missing.");
        if (source.RemainingQuantity > source.Quantity ||
            (source.Status == "Active" && source.RemainingQuantity <= 0))
            throw new InvalidOperationException("The paper position has invalid remaining quantity.");
        var unmirroredPartial = source.PartialExitQuantity - intent.MirroredPartialExitQuantity;
        if (unmirroredPartial < 0 || unmirroredPartial >= intent.FilledQuantity)
            throw new InvalidOperationException("The paper partial exit cannot be reconciled safely.");
        if (unmirroredPartial > 0)
            await MirrorPartialExitAsync(db, intent, unmirroredPartial,
                source.PartialExitPrice ?? source.CurrentPrice, cancellationToken);
        if (source.Status != "Active")
        {
            await MirrorFinalExitAsync(db, intent, source.CurrentPrice, cancellationToken);
            return;
        }
        await MirrorStopUpdateAsync(db, intent, source.StopLoss, cancellationToken);
    }

    private async Task SyncNiftyPositionAsync(TradingDbContext db,
        LiveExecutionIntent intent, CancellationToken cancellationToken)
    {
        var prefix = intent.SourceId.ToString("N");
        var addOn = await ReadPaperActionAsync(db, prefix + "-ADDON", cancellationToken);
        var unmirroredAddOn = addOn.Quantity - intent.MirroredAddOnQuantity;
        if (unmirroredAddOn < 0)
            throw new InvalidOperationException("The paper add-on cannot be reconciled safely.");
        if (unmirroredAddOn > 0)
            await MirrorAddOnAsync(db, intent, unmirroredAddOn, addOn.Price,
                cancellationToken);
        var partial = await ReadPaperActionAsync(db, prefix + "-PARTIAL", cancellationToken);
        var unmirroredPartial = partial.Quantity - intent.MirroredPartialExitQuantity;
        if (unmirroredPartial < 0 || unmirroredPartial >= intent.FilledQuantity)
            throw new InvalidOperationException("The paper partial exit cannot be reconciled safely.");
        if (unmirroredPartial > 0)
            await MirrorPartialExitAsync(db, intent, unmirroredPartial, partial.Price,
                cancellationToken);
        var exit = await ReadPaperActionAsync(db, prefix + "-EXIT", cancellationToken);
        if (exit.Quantity > 0)
        {
            await MirrorFinalExitAsync(db, intent, exit.Price, cancellationToken);
            return;
        }
        var stopJson = await db.MarketStrategyAudits.AsNoTracking().Where(value =>
                value.Market == TradingMarketCatalog.Nifty.Code &&
                value.Outcome == "LiveProtectionDesired:" + prefix)
            .OrderByDescending(value => value.CandleTimeUtc)
            .Select(value => value.ReasonsJson).FirstOrDefaultAsync(cancellationToken);
        if (TryReadDesiredStop(stopJson, out var paperStop))
            await MirrorStopUpdateAsync(db, intent, paperStop, cancellationToken);
    }

    private static async Task<PaperAction> ReadPaperActionAsync(TradingDbContext db,
        string clientReference, CancellationToken cancellationToken)
    {
        var json = await db.PaperBrokerEvents.AsNoTracking().Where(value =>
                value.ClientReference == clientReference && value.EventType == "OrderFilled")
            .OrderByDescending(value => value.Sequence).Select(value => value.PayloadJson)
            .FirstOrDefaultAsync(cancellationToken);
        return TryReadPaperAction(json, out var action) ? action : default;
    }

    internal static bool TryReadPaperAction(string? json, out PaperAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("cumulativeFilledQuantity", out var quantityNode) ||
                !quantityNode.TryGetInt32(out var quantity) || quantity <= 0 ||
                !root.TryGetProperty("averageFillPrice", out var priceNode) ||
                !priceNode.TryGetDecimal(out var price) || price <= 0)
                return false;
            action = new PaperAction(quantity, price);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryReadDesiredStop(string? json, out decimal stop)
    {
        stop = 0m;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("effectiveStop", out var node) &&
                   node.TryGetDecimal(out stop) && stop > .05m;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal readonly record struct PaperAction(int Quantity, decimal Price);

    private async Task MirrorAddOnAsync(TradingDbContext db, LiveExecutionIntent intent,
        int paperQuantity, decimal paperPrice, CancellationToken cancellationToken)
    {
        var instrument = await db.Instruments.AsNoTracking().SingleAsync(value =>
            value.Id == intent.InstrumentId, cancellationToken);
        var action = await SubmitPaperActionAsync(intent, instrument, Direction.Buy,
            paperQuantity, paperPrice, Reference("LA", intent.SourceId), cancellationToken);
        if (action.FilledQuantity <= 0)
            throw new InvalidOperationException("Live add-on did not fill; reconcile paper and broker.");
        await broker.ModifyProtectionAsync(intent.ProtectionId!,
            checked(intent.FilledQuantity + action.FilledQuantity), intent.StopLoss,
            cancellationToken);
        intent.RecordAddOn(action.FilledQuantity, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        if (action.FilledQuantity != paperQuantity)
            throw new InvalidOperationException("Live add-on filled less than its paper source.");
    }

    private async Task MirrorPartialExitAsync(TradingDbContext db,
        LiveExecutionIntent intent, int paperQuantity, decimal paperPrice,
        CancellationToken cancellationToken)
    {
        if (paperQuantity <= 0 || paperQuantity >= intent.FilledQuantity)
            throw new InvalidOperationException("The paper partial is incompatible with live exposure.");
        var reference = Reference("LQ", intent.SourceId);
        var existingAction = await broker.GetOrderAsync(reference, cancellationToken);
        if (existingAction is null &&
            await AvailableManagedQuantityAsync(db, intent, cancellationToken) < intent.FilledQuantity)
            throw new InvalidOperationException("Broker position changed before the paper partial exit.");
        var instrument = await db.Instruments.AsNoTracking().SingleAsync(value =>
            value.Id == intent.InstrumentId, cancellationToken);
        // Reserve only the remaining units for the broker stop before issuing a
        // separate sell. If both orders race to fill, their combined quantities
        // can close but cannot exceed the managed long position.
        if (existingAction is null)
            await broker.ModifyProtectionAsync(intent.ProtectionId!,
                intent.FilledQuantity - paperQuantity, intent.StopLoss,
                cancellationToken);
        var action = await SubmitPaperActionAsync(intent, instrument, Direction.Sell,
            paperQuantity, paperPrice, reference, cancellationToken);
        if (action.FilledQuantity <= 0)
        {
            await broker.ModifyProtectionAsync(intent.ProtectionId!, intent.FilledQuantity,
                intent.StopLoss, cancellationToken);
            throw new InvalidOperationException("Live partial exit did not fill; broker stop remains active.");
        }
        if (action.FilledQuantity < paperQuantity)
            await broker.ModifyProtectionAsync(intent.ProtectionId!,
                intent.FilledQuantity - action.FilledQuantity, intent.StopLoss,
                cancellationToken);
        intent.RecordPartialExit(action.FilledQuantity, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        if (action.FilledQuantity != paperQuantity)
            throw new InvalidOperationException("Live partial exit filled less than its paper source.");
    }

    private async Task MirrorStopUpdateAsync(TradingDbContext db,
        LiveExecutionIntent intent, decimal paperStop, CancellationToken cancellationToken)
    {
        if (paperStop <= .05m || paperStop == intent.StopLoss) return;
        var priorStop = intent.StopLoss;
        var protection = await broker.ModifyProtectionAsync(intent.ProtectionId!, intent.FilledQuantity,
            paperStop, cancellationToken);
        intent.AdjustProtectedExposure(intent.FilledQuantity, paperStop, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await TryRecordLiveEventAsync(db, intent, "StopModified",
            new { priorStop, requestedStop = paperStop,
                requestedLimit = GrowwBrokerGateway.ProtectiveLimit(paperStop),
                protection.BrokerProtectionId, protection.Status, protection.UpdatedAtUtc },
            cancellationToken);
    }

    private async Task MirrorFinalExitAsync(TradingDbContext db,
        LiveExecutionIntent intent, decimal paperPrice, CancellationToken cancellationToken)
    {
        var finalReference = Reference("LX", intent.SourceId);
        var priorExit = await broker.GetOrderAsync(finalReference, cancellationToken);
        var protectionReference = Reference("LP", intent.Id);
        var stop = await broker.GetOrderAsync(protectionReference, cancellationToken)
                   ?? throw new BrokerOrderOutcomeUnknownException(protectionReference);
        if (HasConflictingExitFills(priorExit, stop))
            throw new InvalidOperationException(
                "Both broker exit and stop report fills; reconcile potential over-exit.");
        if (IsConfirmedFinalFill(priorExit, intent.FilledQuantity))
        {
            // A process may have stopped after the broker sold this leg but before
            // the intent was saved. The filled order reference, not aggregate
            // instrument quantity, proves this leg was closed.
            intent.Closed(timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            await TryRecordLiveEventAsync(db, intent, "PaperExitRecovered",
                new { priorExit!.BrokerOrderId, priorExit.FilledQuantity,
                    priorExit.AverageFillPrice, priorExit.UpdatedAtUtc }, cancellationToken);
            return;
        }
        if (stop.FilledQuantity > 0)
        {
            if (stop.State != OrderState.Filled || stop.FilledQuantity != intent.FilledQuantity)
                throw new InvalidOperationException(
                    "Broker stop partially sold this position; reconcile before another sell.");
            intent.Closed(timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            await TryRecordLiveEventAsync(db, intent, "BrokerStopFilled",
                new { stop.BrokerOrderId, stop.FilledQuantity,
                    stop.AverageFillPrice, stop.UpdatedAtUtc }, cancellationToken);
            return;
        }
        var available = await AvailableManagedQuantityAsync(db, intent, cancellationToken);
        if (available < intent.FilledQuantity)
            throw new InvalidOperationException("Broker exposure is smaller than the paper exit.");
        var instrument = await db.Instruments.AsNoTracking().SingleAsync(value =>
            value.Id == intent.InstrumentId, cancellationToken);
        if (stop.State is not OrderState.Cancelled)
        {
            if (stop.State is not (OrderState.BrokerAcknowledged or OrderState.PartiallyFilled))
                throw new InvalidOperationException("The broker stop is not cancellable for a paper exit.");
            await broker.CancelProtectionAsync(intent.ProtectionId!, cancellationToken);
            stop = await broker.GetOrderAsync(protectionReference, cancellationToken)
                   ?? throw new BrokerOrderOutcomeUnknownException(protectionReference);
            if (stop.State != OrderState.Cancelled || stop.FilledQuantity > 0)
                throw new BrokerOrderOutcomeUnknownException(protectionReference);
        }
        // The stop is definitively cancelled before a full sell. A late broker
        // stop fill cannot combine with this order to create a short position.
        var action = await SubmitPaperActionAsync(intent, instrument, Direction.Sell,
            intent.FilledQuantity, paperPrice, finalReference,
            cancellationToken);
        if (action.FilledQuantity != intent.FilledQuantity)
            throw new InvalidOperationException("Live final exit was not fully filled; reconcile the remaining unprotected exposure.");
        intent.Closed(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await TryRecordLiveEventAsync(db, intent, "PaperExitFilled",
            new { paperPrice, requestedLimit = DirectionalLimitPrice(Direction.Sell, paperPrice),
                action.BrokerOrderId, action.FilledQuantity, action.AverageFillPrice,
                action.UpdatedAtUtc }, cancellationToken);
    }

    internal static bool IsConfirmedFinalFill(BrokerOrderSnapshot? order, int expectedQuantity) =>
        order is { State: OrderState.Filled } && expectedQuantity > 0 &&
        order.FilledQuantity == expectedQuantity;

    internal static bool HasConflictingExitFills(BrokerOrderSnapshot? exit,
        BrokerOrderSnapshot stop) => exit is { FilledQuantity: > 0 } &&
                                    stop.FilledQuantity > 0;

    private async Task<int> AvailableManagedQuantityAsync(TradingDbContext db,
        LiveExecutionIntent intent, CancellationToken cancellationToken)
    {
        var positions = await broker.GetPositionsAsync(cancellationToken);
        var actual = positions.Where(value => value.InstrumentId == intent.InstrumentId &&
            value.Direction == intent.Direction).Sum(value => value.Quantity);
        var other = await db.LiveExecutionIntents.AsNoTracking().Where(value =>
                value.Id != intent.Id && value.FilledQuantity > 0 &&
                value.Status != "Closed" && value.Status != "Rejected" &&
                value.InstrumentId == intent.InstrumentId && value.Direction == intent.Direction)
            .SumAsync(value => value.FilledQuantity, cancellationToken);
        return actual - other;
    }

    private async Task<BrokerOrderSnapshot> SubmitPaperActionAsync(LiveExecutionIntent intent,
        Instrument instrument, Direction direction, int quantity, decimal paperPrice,
        string clientReference, CancellationToken cancellationToken)
    {
        if (quantity <= 0 || paperPrice <= 0)
            throw new InvalidOperationException("A paper action has invalid price or quantity.");
        var price = DirectionalLimitPrice(direction, paperPrice);
        var order = await broker.SubmitAsync(new BrokerOrderRequest(clientReference,
            intent.InstrumentId, direction, quantity, price,
            TradingSymbol: instrument.TradingSymbol, Exchange: instrument.Exchange,
            Segment: "FNO", Product: "NRML", OrderType: "LIMIT"), cancellationToken);
        for (var attempt = 0; attempt < 20 && order.FilledQuantity < quantity &&
                 order.State is (OrderState.BrokerAcknowledged or OrderState.PartiallyFilled); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            order = await broker.GetOrderAsync(clientReference, cancellationToken)
                    ?? throw new BrokerOrderOutcomeUnknownException(clientReference);
        }
        if (order.FilledQuantity < quantity &&
            order.State is (OrderState.BrokerAcknowledged or OrderState.PartiallyFilled))
        {
            var cancelled = await broker.CancelAsync(clientReference, cancellationToken);
            if (cancelled.State != OrderState.Cancelled)
                throw new BrokerOrderOutcomeUnknownException(clientReference);
            order = await broker.GetOrderAsync(clientReference, cancellationToken) ?? cancelled;
        }
        return order;
    }

    internal static decimal DirectionalLimitPrice(Direction direction, decimal paperPrice)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(paperPrice);
        var adjusted = direction == Direction.Buy ? paperPrice * 1.02m : paperPrice * .98m;
        var ticks = direction == Direction.Buy
            ? Math.Ceiling(adjusted / .05m) : Math.Floor(adjusted / .05m);
        return Math.Max(.05m, ticks * .05m);
    }

    private async Task<bool> ReconcileAsync(TradingDbContext db, CancellationToken cancellationToken)
    {
        var active = await db.LiveExecutionIntents.Where(value => value.Status == "Protected")
            .ToListAsync(cancellationToken);
        var actual = await broker.GetPositionsAsync(cancellationToken);
        var recentlyClosed = await db.LiveExecutionIntents.AsNoTracking().Where(value =>
                value.Status == "Closed" &&
                value.CreatedAtUtc >= timeProvider.GetUtcNow().AddDays(-1))
            .Select(value => value.InstrumentId).Distinct().ToListAsync(cancellationToken);
        var unsettled = await db.LiveExecutionIntents.AsNoTracking().Where(value =>
                value.Status != "Closed" && value.Status != "Rejected" &&
                value.Status != "Protected")
            .Select(value => value.InstrumentId).Distinct().ToListAsync(cancellationToken);
        foreach (var instrumentId in recentlyClosed.Where(value =>
                     !active.Any(intent => intent.InstrumentId == value) &&
                     !unsettled.Contains(value)))
        {
            if (!actual.Any(value => value.InstrumentId == instrumentId)) continue;
            LogReconciliationBlocked(logger,
                $"Closed managed instrument {instrumentId:N} still has broker exposure.");
            return false;
        }
        var expected = active.Select(value => new ExpectedBrokerPosition(value.InstrumentId,
            value.Direction, value.FilledQuantity)).ToArray();
        var result = ReconcileManagedPositions(expected, actual, timeProvider.GetUtcNow());
        if (result.TradingPermitted) return true;
        LogReconciliationBlocked(logger, string.Join(" ", result.Mismatches));
        return false;
    }

    private async Task<LiveExecutionIntent?> DiscoverNiftyAsync(TradingDbContext db,
        DateTimeOffset armedAtUtc, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddSeconds(-options.Value.SignalMaximumAgeSeconds);
        var filledEntries = await db.PaperBrokerEvents.AsNoTracking()
            .Where(value => value.EventType == "OrderFilled" &&
                            value.ClientReference.EndsWith("-ENTRY") &&
                            value.OccurredAtUtc >= armedAtUtc && value.OccurredAtUtc >= cutoff)
            .OrderBy(value => value.OccurredAtUtc).ToListAsync(cancellationToken);
        foreach (var entry in filledEntries)
        {
            if (!TryParseNiftyEntrySignalId(entry.EventType, entry.ClientReference,
                    out var signalId)) continue;
            if (await db.LiveExecutionIntents.AnyAsync(value => value.SourceType == "NIFTY" &&
                    value.SourceId == signalId, cancellationToken)) continue;
            var signal = await db.Signals.AsNoTracking().SingleOrDefaultAsync(value =>
                value.Id == signalId, cancellationToken);
            if (signal is null) continue;
            if (await db.PaperBrokerEvents.AsNoTracking().AnyAsync(value =>
                    value.ClientReference == $"{signalId:N}-EXIT" &&
                    value.EventType == "OrderFilled", cancellationToken))
            {
                var outcome = $"LiveParityMissed:{signalId:N}";
                if (!await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                        value.Market == TradingMarketCatalog.Nifty.Code &&
                        value.Outcome == outcome, cancellationToken))
                {
                    db.MarketStrategyAudits.Add(new MarketStrategyAudit(Guid.NewGuid(),
                        TradingMarketCatalog.Nifty.Code, signal.InstrumentId,
                        timeProvider.GetUtcNow(), outcome, 0m,
                        JsonSerializer.Serialize(new
                        {
                            signalId,
                            reason = "Paper trade exited before live discovery; no stale live entry was submitted."
                        })));
                    await db.SaveChangesAsync(cancellationToken);
                }
                continue;
            }
            var risk = await db.RiskDecisions.AsNoTracking().SingleOrDefaultAsync(value =>
                value.SignalId == signalId && value.Approved, cancellationToken);
            if (risk is null || !TryReadOptionProposal(risk.SnapshotJson, out var proposal)) continue;
            if (!TryReadPaperFilledQuantity(entry.PayloadJson, out var paperQuantity)) continue;
            var instrument = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(value =>
                value.Id == proposal.InstrumentId && value.IsActive, cancellationToken);
            if (instrument is null || instrument.Exchange != "NSE") continue;
            if (paperQuantity % instrument.LotSize != 0) continue;
            return NewIntent("NIFTY", "NIFTY", signal.Id, instrument, paperQuantity, proposal.EntryPrice,
                proposal.StopLoss, proposal.Target);
        }
        return null;
    }

    private async Task<LiveExecutionIntent?> DiscoverMarketPositionAsync(TradingDbContext db,
        string market, DateTimeOffset armedAtUtc, CancellationToken cancellationToken)
    {
        // Retired market: continue managing existing live intents, but never submit new Bankex entries.
        if (market.Equals("bankex", StringComparison.OrdinalIgnoreCase)) return null;
        if (!options.Value.AllowedMarkets.Contains(market.ToUpperInvariant(),
                StringComparer.OrdinalIgnoreCase)) return null;
        var cutoff = timeProvider.GetUtcNow().AddSeconds(-options.Value.SignalMaximumAgeSeconds);
        var sources = await db.MarketPaperPositions.AsNoTracking()
            .Where(value => value.Market == market && value.Status == "Active" &&
                            value.OpenedAtUtc >= armedAtUtc && value.OpenedAtUtc >= cutoff)
            .OrderBy(value => value.OpenedAtUtc).ToListAsync(cancellationToken);
        foreach (var source in sources)
        {
            if (!IsLiveBrokerProtectable(source.Strategy, source.StopLoss))
            {
                if (source.Strategy.StartsWith(IntradayExpansionPairService.StrategyPrefix,
                        StringComparison.Ordinal) ||
                    source.Strategy.StartsWith("PaperOnly|", StringComparison.Ordinal))
                {
                    var researchOnly = source.Strategy.StartsWith("PaperOnly|", StringComparison.Ordinal);
                    var outcome = $"LiveParityException:{source.Id:N}";
                    if (!await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                            value.Market == market && value.Outcome == outcome, cancellationToken))
                    {
                        db.MarketStrategyAudits.Add(new MarketStrategyAudit(Guid.NewGuid(), market,
                            source.UnderlyingInstrumentId, timeProvider.GetUtcNow(), outcome,
                            0m, JsonSerializer.Serialize(new
                            {
                                sourcePositionId = source.Id,
                                reason = researchOnly
                                    ? "Research-only strategy awaits explicit live promotion."
                                    : "Expansion-pair floor stop cannot be broker-protected; live pair blocked by owner choice."
                            })));
                        await db.SaveChangesAsync(cancellationToken);
                    }
                }
                continue;
            }
            if (TryOtherPairedLeg(source.Strategy, out var otherStrategy))
            {
                var other = await db.MarketPaperPositions.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.Market == market &&
                        value.Strategy == otherStrategy && value.OpenedAtUtc == source.OpenedAtUtc,
                        cancellationToken);
                if (other is null || other.Status != "Active" ||
                    !IsLiveBrokerProtectable(other.Strategy, other.StopLoss))
                    continue;
                var otherLiveStatus = await db.LiveExecutionIntents.AsNoTracking().Where(value =>
                        value.SourceType == "MARKET_POSITION" && value.SourceId == other.Id)
                    .Select(value => value.Status).FirstOrDefaultAsync(cancellationToken);
                if (otherLiveStatus is "Rejected" or "Closed" or "ReconciliationRequired")
                    continue;
            }
            if (await db.LiveExecutionIntents.AnyAsync(value => value.SourceType == "MARKET_POSITION" &&
                    value.SourceId == source.Id, cancellationToken)) continue;
            var instrument = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(value =>
                value.Id == source.ExecutionInstrumentId && value.IsActive, cancellationToken);
            var expectedExchange = market is "nifty" or "bank-nifty" ? "NSE" : "BSE";
            if (instrument is null || instrument.Exchange != expectedExchange ||
                source.Quantity <= 0 || instrument.LotSize <= 0) continue;
            if (source.Quantity % instrument.LotSize != 0) continue;
            return NewIntent("MARKET_POSITION", market.ToUpperInvariant(), source.Id, instrument,
                source.Quantity, source.EntryPrice,
                source.StopLoss, source.Target);
        }
        return null;
    }

    private LiveExecutionIntent NewIntent(string sourceType, string market, Guid sourceId,
        Instrument instrument,
        int quantity, decimal entry, decimal stop, decimal target)
    {
        var id = Guid.NewGuid();
        return new LiveExecutionIntent(id, sourceType, sourceId, market, instrument.Id, Direction.Buy,
            quantity, entry, stop, target, LiveClientReference(sourceId), timeProvider.GetUtcNow());
    }

    internal static string LiveClientReference(Guid sourceId) => Reference("LE", sourceId);

    internal static bool IsDefinitivePreSubmissionRejection(LiveExecutionIntent intent) =>
        intent.Status == "ReconciliationRequired" && intent.BrokerOrderId is null &&
        intent.LastError is { } error &&
        (error.Contains("returned 400", StringComparison.Ordinal) ||
         error.Contains("returned 401", StringComparison.Ordinal) ||
         error.Contains("returned 403", StringComparison.Ordinal) ||
         error.Contains("returned 422", StringComparison.Ordinal));

    internal static bool IsLiveBrokerProtectable(string strategy, decimal stopLoss) =>
        !strategy.StartsWith("PaperOnly|", StringComparison.Ordinal) && stopLoss > .05m;

    internal static bool TryOtherPairedLeg(string strategy, out string otherStrategy)
    {
        otherStrategy = string.Empty;
        var prefix = strategy.StartsWith("Hero Zero|", StringComparison.Ordinal)
            ? "Hero Zero|"
            : strategy.StartsWith(IntradayExpansionPairService.StrategyPrefix,
                StringComparison.Ordinal)
                ? IntradayExpansionPairService.StrategyPrefix
                : null;
        if (prefix is null) return false;
        var suffix = strategy.LastIndexOf('|');
        if (suffix < prefix.Length) return false;
        var side = strategy[(suffix + 1)..];
        if (side is not ("CE" or "PE")) return false;
        otherStrategy = strategy[..(suffix + 1)] + (side == "CE" ? "PE" : "CE");
        return true;
    }

    private async Task SubmitAndProtectAsync(TradingDbContext db, LiveExecutionIntent intent,
        CancellationToken cancellationToken)
    {
        var instrument = await db.Instruments.AsNoTracking().SingleAsync(value =>
            value.Id == intent.InstrumentId, cancellationToken);
        try
        {
            if (intent.StopLoss <= .05m)
                throw new InvalidOperationException("Paper stop cannot be protected by a valid stop-limit order.");
            BrokerOrderSnapshot order;
            if (intent.Status == "Pending")
            {
                if (intent.SourceType == "MARKET_POSITION")
                {
                    var paperStillOpen = await db.MarketPaperPositions.AsNoTracking().AnyAsync(value =>
                        value.Id == intent.SourceId && value.Status == "Active" &&
                        value.RemainingQuantity > 0, cancellationToken);
                    if (!paperStillOpen)
                    {
                        intent.Reject("Paper position closed before live entry submission.",
                            timeProvider.GetUtcNow());
                        await db.SaveChangesAsync(cancellationToken);
                        return;
                    }
                }
                else if (intent.SourceType == "NIFTY" &&
                         await db.PaperBrokerEvents.AsNoTracking().AnyAsync(value =>
                             value.ClientReference == $"{intent.SourceId:N}-EXIT" &&
                             value.EventType == "OrderFilled", cancellationToken))
                {
                    intent.Reject("Paper position closed before live entry submission.",
                        timeProvider.GetUtcNow());
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
                var sameContract = await broker.GetPositionsAsync(cancellationToken);
                var managed = await db.LiveExecutionIntents.AsNoTracking().Where(value =>
                        value.Id != intent.Id && value.InstrumentId == intent.InstrumentId &&
                        value.Status == "Protected")
                    .Select(value => new { value.Direction, value.FilledQuantity })
                    .ToListAsync(cancellationToken);
                if (!HasOnlyManagedExposure(intent.InstrumentId,
                        managed.Select(value => new ExpectedBrokerPosition(intent.InstrumentId,
                            value.Direction, value.FilledQuantity)).ToArray(), sameContract))
                    throw new InvalidOperationException(
                        "Broker contract has unmanaged exposure; reconcile before opening a live entry.");
                intent.BeginEntrySubmission();
                await db.SaveChangesAsync(cancellationToken);
                order = await broker.SubmitAsync(new BrokerOrderRequest(intent.ClientReference,
                    instrument.Id, intent.Direction, intent.Quantity,
                    DirectionalLimitPrice(intent.Direction, intent.RequestedEntry),
                    TradingSymbol: instrument.TradingSymbol, Exchange: instrument.Exchange,
                    Segment: "FNO", Product: "NRML", OrderType: "LIMIT"), cancellationToken);
            }
            else
                order = await broker.GetOrderAsync(intent.ClientReference, cancellationToken)
                        ?? throw new BrokerOrderOutcomeUnknownException(intent.ClientReference);
            intent.Submitted(order.BrokerOrderId, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            for (var attempt = 0; attempt < 20 &&
                     (order.FilledQuantity == 0 || order.AverageFillPrice is null) && order.State is not
                     (OrderState.Cancelled or OrderState.RejectedByBroker or
                      OrderState.ReconciliationRequired); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                order = await broker.GetOrderAsync(intent.ClientReference, cancellationToken)
                        ?? throw new BrokerOrderOutcomeUnknownException(intent.ClientReference);
            }
            if (order.FilledQuantity <= 0)
            {
                if (order.FilledQuantity == 0 && order.State is
                    (OrderState.RejectedByBroker or OrderState.Cancelled))
                {
                    intent.Reject("Entry was declined or cancelled without a broker fill.",
                        timeProvider.GetUtcNow());
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
                if (order.State is OrderState.BrokerAcknowledged or OrderState.PartiallyFilled)
                {
                    var cancelled = await broker.CancelAsync(intent.ClientReference, cancellationToken);
                    if (cancelled.State != OrderState.Cancelled)
                        throw new BrokerOrderOutcomeUnknownException(intent.ClientReference);
                    intent.Reject("Entry limit order was not filled before its timeout.", timeProvider.GetUtcNow());
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
                throw new BrokerOrderOutcomeUnknownException(intent.ClientReference);
            }
            if (order.FilledQuantity < order.RequestedQuantity)
            {
                var cancelled = await broker.CancelAsync(intent.ClientReference, cancellationToken);
                if (cancelled.State != OrderState.Cancelled)
                    throw new BrokerOrderOutcomeUnknownException(intent.ClientReference);
                order = await broker.GetOrderAsync(intent.ClientReference, cancellationToken) ?? cancelled;
                if (order.State != OrderState.Cancelled || order.FilledQuantity <= 0)
                    throw new BrokerOrderOutcomeUnknownException(intent.ClientReference);
            }
            var fillPriceMissing = order.AverageFillPrice is null;
            // Groww can report a filled quantity before its trades endpoint has a
            // price. Protect the real position using the paper stop anyway, then
            // block further live entries until its fill price is reconciled.
            intent.Filled(order.FilledQuantity,
                order.AverageFillPrice ?? intent.RequestedEntry);
            await db.SaveChangesAsync(cancellationToken);
            intent.BeginProtection();
            await db.SaveChangesAsync(cancellationToken); // crash here blocks duplicate stop creation
            var protection = await broker.CreateProtectionAsync(new BrokerProtectionRequest(
                Reference("LP", intent.Id), instrument.Id, instrument.TradingSymbol,
                instrument.Exchange, order.FilledQuantity, intent.StopLoss),
                cancellationToken);
            intent.Protected(protection.BrokerProtectionId, timeProvider.GetUtcNow());
            if (fillPriceMissing)
                intent.FlagParityMismatch("Broker fill price is unavailable; protected exposure needs reconciliation.");
            if (order.FilledQuantity != intent.Quantity)
                intent.FlagParityMismatch("Broker entry only partially filled; paper quantity differs from live exposure.");
            await MarkControlledTestCompleteAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await TryRecordLiveEventAsync(db, intent, "EntryFilledAndProtected",
                new { order.BrokerOrderId, order.FilledQuantity,
                    order.AverageFillPrice, entryObservedAtUtc = order.UpdatedAtUtc,
                    protection.BrokerProtectionId, protection.StopLoss,
                    protectiveLimit = GrowwBrokerGateway.ProtectiveLimit(protection.StopLoss),
                    protectionObservedAtUtc = protection.UpdatedAtUtc }, cancellationToken);
            // The paper source may have closed while Groww filled and protected
            // the entry. Recheck its durable lifecycle without another poll delay.
            wakeSignal.Notify();
        }
        catch (GrowwApiException exception) when (intent.BrokerOrderId is null &&
                                                   exception.StatusCode is 400 or 401 or 403 or 422)
        {
            intent.Reject(exception.Message, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            throw;
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

    private async Task TryRecordLiveEventAsync(TradingDbContext db,
        LiveExecutionIntent intent, string eventName, object details,
        CancellationToken cancellationToken)
    {
        var atUtc = timeProvider.GetUtcNow();
        var audit = new MarketStrategyAudit(Guid.NewGuid(), intent.Market.ToLowerInvariant(),
            intent.InstrumentId, atUtc, "LiveExecution:" + eventName, 0m,
            JsonSerializer.Serialize(new
            {
                version = 1,
                sourceIdCompact = intent.SourceId.ToString("N"),
                intentId = intent.Id,
                intent.SourceType,
                intent.Market,
                intent.Status,
                observedAtUtc = atUtc,
                details
            }));
        try
        {
            db.MarketStrategyAudits.Add(audit);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.Entry(audit).State = EntityState.Detached;
            LogTelemetryPersistenceFailed(logger, exception, intent.Id);
        }
    }

    private static string Reference(string prefix, Guid id) => prefix + id.ToString("N")[..16];

    internal static bool TryParseNiftyEntrySignalId(string eventType, string clientReference,
        out Guid signalId)
    {
        signalId = Guid.Empty;
        const string suffix = "-ENTRY";
        return eventType == "OrderFilled" && clientReference.EndsWith(suffix, StringComparison.Ordinal) &&
               Guid.TryParseExact(clientReference[..^suffix.Length], "N", out signalId);
    }

    internal static BrokerReconciliationResult ReconcileManagedPositions(
        IReadOnlyCollection<ExpectedBrokerPosition> expectedPositions,
        IReadOnlyCollection<BrokerPositionSnapshot> actualPositions,
        DateTimeOffset observedAtUtc)
    {
        var expectedMap = expectedPositions.GroupBy(value => (value.InstrumentId, value.Direction))
            .ToDictionary(group => group.Key, group => group.Sum(value => value.Quantity));
        var actualMap = actualPositions.GroupBy(value => (value.InstrumentId, value.Direction))
            .ToDictionary(group => group.Key, group => group.Sum(value => value.Quantity));
        var mismatches = new List<string>();
        foreach (var key in expectedMap.Keys)
        {
            actualMap.TryGetValue(key, out var observed);
            var expected = expectedMap[key];
            if (expected != observed)
                mismatches.Add($"{key.InstrumentId:N}/{key.Direction}: expected {expected}, actual {observed}.");
        }
        return new BrokerReconciliationResult(mismatches.Count == 0, mismatches.Count == 0,
            mismatches, observedAtUtc);
    }

    internal static bool HasOnlyManagedExposure(Guid instrumentId,
        IReadOnlyCollection<ExpectedBrokerPosition> managed,
        IReadOnlyCollection<BrokerPositionSnapshot> actual)
    {
        foreach (var direction in new[] { Direction.Buy, Direction.Sell })
        {
            if (managed.Where(value => value.InstrumentId == instrumentId &&
                    value.Direction == direction).Sum(value => value.Quantity) !=
                actual.Where(value => value.InstrumentId == instrumentId &&
                    value.Direction == direction).Sum(value => value.Quantity)) return false;
        }
        return true;
    }

    internal static bool TryReadOptionProposal(string json, out OptionProposal proposal)
    {
        proposal = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("optionProposal", out var node) ||
                node.ValueKind != JsonValueKind.Object) return false;
            proposal = new(node.GetProperty("instrumentId").GetGuid(),
                node.GetProperty("proposedEntry").GetDecimal(),
                node.GetProperty("proposedStopLoss").GetDecimal(),
                node.GetProperty("proposedTarget").GetDecimal());
            return proposal.InstrumentId != Guid.Empty && proposal.EntryPrice > 0 &&
                   proposal.StopLoss > 0 && proposal.Target > 0;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    internal static bool TryReadPaperFilledQuantity(string json, out int quantity)
    {
        quantity = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("cumulativeFilledQuantity", out var node) ||
                !node.TryGetInt32(out quantity) || quantity <= 0)
            {
                quantity = 0;
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal readonly record struct OptionProposal(Guid InstrumentId, decimal EntryPrice,
        decimal StopLoss, decimal Target);

    [LoggerMessage(LogLevel.Error, "Automatic live execution cycle failed closed.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Critical, "Live broker reconciliation blocked entries: {mismatches}")]
    private static partial void LogReconciliationBlocked(ILogger logger, string mismatches);

    [LoggerMessage(LogLevel.Warning, "Live execution telemetry persistence failed for intent {intentId}")]
    private static partial void LogTelemetryPersistenceFailed(ILogger logger,
        Exception exception, Guid intentId);
}
