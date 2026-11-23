using Microsoft.EntityFrameworkCore;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed partial class AutomaticLiveExecutionService
{
    private async Task ReconcileExternalExitsAsync(TradingDbContext db, CancellationToken token)
    {
        var all = await OwnIntents(db).Where(i => i.Status != "Rejected" &&
            (i.Status != "Closed" || i.CreatedAtUtc >= timeProvider.GetUtcNow().AddDays(-1))).ToListAsync(token);
        var candidates = all.Where(i => i.Status is "Protected" or "ProtectedScaleMismatch" or "FinalExitPending").ToArray();
        if (candidates.Length == 0) return;
        // Failure here deliberately prevents this cycle from issuing lifecycle
        // actions. Existing confirmed broker stops remain in place.
        var snapshot = await broker.ReadAccountAsync(token);
        var references = all.SelectMany(i => new[] { i.ClientReference,
            Reference("LP", i.Id), Reference("LR", i.Id), Reference("LX", i.SourceId),
            Reference("LA", i.SourceId), Reference("LQ", i.SourceId) }).ToHashSet(StringComparer.Ordinal);
        foreach (var intent in candidates)
        {
            var instrument = await db.Instruments.AsNoTracking().SingleAsync(i => i.Id == intent.InstrumentId, token);
            var owners = all.Count(i => i.InstrumentId == intent.InstrumentId && i.Status != "Closed");
            try
            {
                var previousError = intent.LastError;
                var okay = await LiveExternalExitReconciler.ReconcileAsync(intent, instrument.TradingSymbol,
                    instrument.Exchange, snapshot, owners, references, broker,
                    async ct => { await db.SaveChangesAsync(ct); }, token,
                    all.Where(i => i.InstrumentId == intent.InstrumentId && i.Status != "Closed").Sum(i => i.FilledQuantity),
                    (verifiedStop, ct) => TryRecordLiveEventAsync(db, intent, "ExternalExitReconciled",
                        new { intent.ExternalExitQuantity, remaining = intent.FilledQuantity,
                            verifiedStop,
                            reason = $"Exited in {broker.Broker}; paper simulation unchanged.",
                            scaling = "Further automatic scaling held; remaining SL and final exit stay managed." }, ct));
                if (!okay && intent.LastError != previousError)
                    await TryRecordLiveEventAsync(db, intent, "ExternalExitNeedsReconciliation",
                        new { intent.LastError }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                intent.BeginExternalReconciliation();
                intent.RecordExitUncertainty("External-exit verification incomplete; automatic actions held.");
                await db.SaveChangesAsync(token);
                LogCycleFailed(logger, exception);
            }
        }
    }
}
