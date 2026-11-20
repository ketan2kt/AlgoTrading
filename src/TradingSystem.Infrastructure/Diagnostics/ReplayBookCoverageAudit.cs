using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Infrastructure.Diagnostics;

internal sealed record ReplayContractBookCoverage(string Symbol, double PrimaryMissingSeconds,
    double CombinedMissingSeconds, DateTimeOffset? FirstUncoveredAtUtc);

// Evidence only: a standing book can cover at most the existing 30-second source-age
// window. This neither invents a fill nor treats unchanged size as new liquidity.
internal static class ReplayBookCoverageAudit
{
    internal static IReadOnlyList<ReplayContractBookCoverage> Read(HistoricalReplayArchiveSlice slice,
        CancellationToken cancellationToken = default)
    {
        var session = slice.Session;
        var symbols = slice.RecordedContractUniverse?.Instruments
            .Where(value => value.Market == session.IndexSymbol && value.Type is InstrumentType.CallOption or InstrumentType.PutOption)
            .Select(value => value.TradingSymbol) ?? slice.OptionContracts.Select(value => value.TradingSymbol);
        var states = symbols.Distinct(StringComparer.Ordinal).ToDictionary(symbol => symbol,
            _ => (Primary: new Coverage(session.StartUtc), Combined: new Coverage(session.StartUtc)), StringComparer.Ordinal);
        foreach (var quote in HistoricalQuotePaths.Ordered(session.Quotes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!states.TryGetValue(quote.Symbol, out var state) || quote.AvailableAtUtc < session.StartUtc ||
                quote.AvailableAtUtc >= session.EndUtc || quote.LastPrice <= 0 || !quote.HasExecutableBook() ||
                quote.BidQuantity is not > 0 || quote.OfferQuantity is not > 0 ||
                quote.SourceAtUtc is not { } source || source > quote.AvailableAtUtc) continue;
            var primary = quote.Source == "GrowwFeedSample";
            if (primary)
            {
                if (quote.DepthSourceAtUtc is not { } depth || depth > quote.AvailableAtUtc) continue;
                if (depth < source) source = depth;
            }
            else if (quote.Source != HistoricalQuoteObservation.UpstoxSnapshot) continue;
            var until = source.AddSeconds(30);
            if (until <= quote.AvailableAtUtc) continue;
            if (until > session.EndUtc) until = session.EndUtc;
            state.Combined.Add(quote.AvailableAtUtc, until);
            if (primary) state.Primary.Add(quote.AvailableAtUtc, until);
        }
        return states.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value =>
        {
            value.Value.Primary.Finish(session.EndUtc);
            value.Value.Combined.Finish(session.EndUtc);
            return new ReplayContractBookCoverage(value.Key, value.Value.Primary.MissingSeconds,
                value.Value.Combined.MissingSeconds, value.Value.Combined.FirstGap);
        }).ToArray();
    }

    private sealed class Coverage(DateTimeOffset start)
    {
        private DateTimeOffset coveredUntil = start;
        public double MissingSeconds { get; private set; }
        public DateTimeOffset? FirstGap { get; private set; }
        public void Add(DateTimeOffset at, DateTimeOffset until)
        {
            if (at > coveredUntil)
            {
                FirstGap ??= coveredUntil;
                MissingSeconds += (at - coveredUntil).TotalSeconds;
            }
            if (until > coveredUntil) coveredUntil = until;
        }
        public void Finish(DateTimeOffset end) => Add(end, end);
    }
}
