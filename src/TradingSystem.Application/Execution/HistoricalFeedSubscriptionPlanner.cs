using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalFeedInstrument(string Market, string Exchange, string Segment,
    string ExchangeToken, string TradingSymbol, InstrumentType Type, DateOnly? ExpiryDate,
    decimal? StrikePrice, int LotSize, decimal TickSize);

public static class HistoricalFeedSubscriptionPlanner
{
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    // Prefer the full selected expiry. If it exceeds the broker limit, use a
    // deterministic pre-session band, never a corridor chosen after the session.
    public static IReadOnlyList<HistoricalFeedInstrument> Select(DateOnly tradingDate,
        IReadOnlyList<HistoricalFeedInstrument> instruments, int maximumInstruments = 1000,
        IReadOnlyDictionary<string, decimal>? preSessionAnchors = null,
        NiftyOptionSelectionOptions? niftySelection = null)
    {
        if (maximumInstruments is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumInstruments));
        var selected = new List<HistoricalFeedInstrument>();
        foreach (var market in Markets)
        {
            var rows = instruments.Where(value => value.Market == market).ToArray();
            var index = rows.SingleOrDefault(value => value.Type == InstrumentType.Index)
                ?? throw new InvalidOperationException("A normal index is missing from the streaming master.");
            selected.Add(index);
            if (market != "BANKNIFTY")
                selected.Add(rows.Where(value => value.Type == InstrumentType.Future && value.ExpiryDate >= tradingDate)
                    .OrderBy(value => value.ExpiryDate).ThenBy(value => value.TradingSymbol, StringComparer.Ordinal).FirstOrDefault()
                    ?? throw new InvalidOperationException("A required confirmation future is missing from the streaming master."));
            var contracts = ReplayContractCoverage.Contracts(rows);
            var calls = RegularOptionContractSelector.Eligible(contracts, market, tradingDate, Direction.Buy, niftySelection);
            var puts = RegularOptionContractSelector.Eligible(contracts, market, tradingDate, Direction.Sell, niftySelection);
            if (calls.Count == 0 || puts.Count == 0)
                throw new InvalidOperationException(market + ": both valid CE and PE expiries are required by the shared contract policy.");
            var eligible = calls.Concat(puts).ToArray();
            var symbols = eligible.Select(c => c.TradingSymbol).ToHashSet(StringComparer.Ordinal);
            selected.AddRange(rows.Where(value => symbols.Contains(value.TradingSymbol)));
        }
        if (selected.Any(value => value.Exchange is not ("NSE" or "BSE") ||
                value.Segment is not ("CASH" or "FNO") || string.IsNullOrWhiteSpace(value.ExchangeToken) ||
                value.ExchangeToken.Any(character => !char.IsAsciiLetterOrDigit(character)) ||
                value.Type != InstrumentType.Index && (value.LotSize <= 0 || value.TickSize <= 0m) ||
                value.Type is InstrumentType.CallOption or InstrumentType.PutOption && value.StrikePrice is not > 0m) ||
            selected.Where(value => value.Type is InstrumentType.CallOption or InstrumentType.PutOption)
                .GroupBy(value => (value.Market, value.StrikePrice, value.Type)).Any(group => group.Count() != 1) ||
            selected.GroupBy(value => (value.Exchange, value.Segment, value.ExchangeToken)).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Replay capture instrument metadata is invalid or ambiguous.");
        if (selected.Count > maximumInstruments)
        {
            if (preSessionAnchors is null || Markets.Any(
                    market => !preSessionAnchors.TryGetValue(market, out var anchor) || anchor <= 0m))
                throw new InvalidOperationException("Replay capture exceeds the subscription budget and requires all three pre-session price anchors.");
            var mandatory = selected.Where(value => value.Type is InstrumentType.Index or InstrumentType.Future).ToList();
            var pairsPerMarket = (maximumInstruments - mandatory.Count) / 6;
            if (pairsPerMarket < 3) throw new InvalidOperationException("Replay subscription budget cannot cover three near-ATM strike bands.");
            foreach (var market in Markets)
            {
                // This is an explicitly recorded band, NOT a claim that all listed
                // strikes were captured. Both directions and every strike between
                // its boundaries are retained; replay checks its actual selection.
                var groups = selected.Where(value => value.Market == market &&
                        value.Type is InstrumentType.CallOption or InstrumentType.PutOption)
                    .GroupBy(value => value.StrikePrice).OrderBy(group => Math.Abs(group.Key!.Value - preSessionAnchors[market]))
                    .ThenBy(group => group.Key).Take(pairsPerMarket).ToArray();
                mandatory.AddRange(groups.SelectMany(group => group));
            }
            selected = mandatory;
            if (selected.Count > maximumInstruments)
                throw new InvalidOperationException("Replay strike-band metadata exceeds the subscription budget.");
        }
        return selected.OrderBy(value => value.Exchange, StringComparer.Ordinal).ThenBy(value => value.Segment, StringComparer.Ordinal)
            .ThenBy(value => value.ExchangeToken, StringComparer.Ordinal).ToArray();
    }
}
