namespace TradingSystem.Application.MarketData;

public sealed record FuturesFeedHealthSnapshot(
    bool Available,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    string? ErrorCode,
    string? ErrorDetail);

public interface IFuturesFeedHealthReader
{
    FuturesFeedHealthSnapshot GetCurrent();
}

public sealed class FuturesFeedHealthMonitor : IFuturesFeedHealthReader
{
    private readonly object sync = new();
    private FuturesFeedHealthSnapshot snapshot = new(false, null, null, "NOT_STARTED",
        "The futures feed has not completed a successful cycle.");

    public void RecordSuccess(DateTimeOffset observedAtUtc)
    {
        lock (sync)
            snapshot = new(true, observedAtUtc, snapshot.LastFailureUtc, null, null);
    }

    public void RecordFailure(DateTimeOffset observedAtUtc, string errorCode, string errorDetail)
    {
        lock (sync)
            snapshot = new(false, snapshot.LastSuccessUtc, observedAtUtc,
                errorCode[..Math.Min(errorCode.Length, 80)],
                errorDetail[..Math.Min(errorDetail.Length, 300)]);
    }

    public FuturesFeedHealthSnapshot GetCurrent()
    {
        lock (sync) return snapshot;
    }
}
