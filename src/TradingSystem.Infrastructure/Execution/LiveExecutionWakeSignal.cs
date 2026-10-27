namespace TradingSystem.Infrastructure.Execution;

/// <summary>
/// Coalesces paper-entry notifications so the live mirror checks committed paper
/// sources promptly. The database remains authoritative; this is only a wake-up.
/// </summary>
internal sealed class LiveExecutionWakeSignal : IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);

    public void Notify()
    {
        try { signal.Release(); }
        catch (SemaphoreFullException) { } // multiple paper legs share one wake-up
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => signal.Dispose();
}
