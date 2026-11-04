namespace TradingSystem.Application.Execution;

public static class HistoricalOptionEligibility
{
    // The provider's historical contract list alone says nothing about when
    // an option began trading. A pre-decision observation is mandatory before
    // a research policy may propose a counterfactual entry in that contract.
    public static bool HasFreshAsOfPrice(HistoricalReplayFrame frame,
        string symbol, TimeSpan maximumAge)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrWhiteSpace(symbol) || maximumAge <= TimeSpan.Zero)
            return false;
        return frame.LatestQuotes.TryGetValue(symbol, out var quote) &&
            quote.LastPrice > 0m && quote.AvailableAtUtc < frame.AvailableAtUtc &&
            quote.IsFreshAsOf(frame.AvailableAtUtc, maximumAge);
    }
}
