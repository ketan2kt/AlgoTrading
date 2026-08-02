namespace TradingSystem.Application.Regime;

public sealed class MarketRegimeMonitor : IMarketRegimeReader
{
    private readonly object syncRoot = new();
    private MarketRegimeResult? latest;
    public void Update(MarketRegimeResult result) { lock (syncRoot) latest = result; }
    public MarketRegimeResult? GetLatest() { lock (syncRoot) return latest; }
}

public sealed class MarketRegimeService(MarketRegimeEngine engine,
    IMarketRegimePersistence persistence, MarketRegimeMonitor monitor)
{
    public async Task<MarketRegimeResult> EvaluateAsync(MarketRegimeInput input,
        CancellationToken cancellationToken)
    {
        var result = engine.Evaluate(input);
        await persistence.PersistAsync(input.TradingSessionId, result, cancellationToken);
        monitor.Update(result);
        return result;
    }
}
