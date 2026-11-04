using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalFeedInstrument(string Market, string Exchange, string Segment,
    string ExchangeToken, string TradingSymbol, InstrumentType Type, DateOnly? ExpiryDate,
    decimal? StrikePrice, int LotSize, decimal TickSize);

public static class HistoricalFeedSubscriptionPlanner
{
    // Capture every strike in each normal engine's selected expiry, not just
    // yesterday's trades or a corridor chosen after seeing today's range.
    public static IReadOnlyList<HistoricalFeedInstrument> Select(DateOnly tradingDate,
        IReadOnlyList<HistoricalFeedInstrument> instruments, int maximumInstruments = 1000)
    {
        if (maximumInstruments is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumInstruments));
        var selected = new List<HistoricalFeedInstrument>();
        foreach (var market in new[] { "NIFTY", "SENSEX", "BANKNIFTY" })
        {
            var rows = instruments.Where(value => value.Market == market).ToArray();
            var index = rows.SingleOrDefault(value => value.Type == InstrumentType.Index)
                ?? throw new InvalidOperationException("A normal index is missing from the streaming master.");
            selected.Add(index);
            if (market != "BANKNIFTY")
                selected.Add(rows.Where(value => value.Type == InstrumentType.Future && value.ExpiryDate >= tradingDate)
                    .OrderBy(value => value.ExpiryDate).ThenBy(value => value.TradingSymbol, StringComparer.Ordinal).FirstOrDefault()
                    ?? throw new InvalidOperationException("A required confirmation future is missing from the streaming master."));
            var options = rows.Where(value => value.Type is InstrumentType.CallOption or InstrumentType.PutOption &&
                value.ExpiryDate >= tradingDate.AddDays(1)).ToArray();
            var expiry = options.Select(value => value.ExpiryDate).Min()
                ?? throw new InvalidOperationException("A normal option expiry is missing from the streaming master.");
            selected.AddRange(options.Where(value => value.ExpiryDate == expiry));
        }
        if (selected.Count > maximumInstruments || selected.Any(value => value.Exchange is not ("NSE" or "BSE") ||
                value.Segment is not ("CASH" or "FNO") || string.IsNullOrWhiteSpace(value.ExchangeToken) ||
                value.ExchangeToken.Any(character => !char.IsAsciiLetterOrDigit(character)) ||
                value.Type != InstrumentType.Index && (value.LotSize <= 0 || value.TickSize <= 0m)) ||
            selected.GroupBy(value => (value.Exchange, value.Segment, value.ExchangeToken)).Any(group => group.Count() != 1))
            throw new InvalidOperationException("The complete normal universe exceeds Groww's subscription limit or has invalid/ambiguous metadata; it was not silently truncated.");
        return selected.OrderBy(value => value.Exchange, StringComparer.Ordinal).ThenBy(value => value.Segment, StringComparer.Ordinal)
            .ThenBy(value => value.ExchangeToken, StringComparer.Ordinal).ToArray();
    }
}
