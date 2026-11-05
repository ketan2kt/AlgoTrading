using Microsoft.EntityFrameworkCore;

namespace TradingSystem.Infrastructure.Execution;

// The operation must own a NEW scope/context for every invocation. Committed
// paper lifecycle remains the sole source for live mirroring; no broker calls here.
internal static class PositionManagementRecovery
{
    internal static async Task<bool> RunAsync(Func<bool, Task> operation,
        Func<Exception, string, Task> record, CancellationToken token)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { await operation(false); return true; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (DbUpdateConcurrencyException exception)
            {
                await record(exception, "ConcurrencyReload");
                if (attempt == 0) continue;
            }
            catch (Exception exception)
            {
                await record(exception, "AnalysisOrPersistence");
            }
            break;
        }
        // An analysis error must not prevent premium stops, manual/time exits or
        // existing target exits. Recovery reloads, never resurrects closed trades,
        // never loosens stops, and never opens a new trade or partial/add-on.
        try { token.ThrowIfCancellationRequested(); await operation(true); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { await record(exception, "ProtectiveRecoveryFailed"); }
        return false;
    }
}
