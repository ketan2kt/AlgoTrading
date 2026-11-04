namespace TradingSystem.Application.Execution;

public sealed record HistoricalReplayArchiveCoverage(DateOnly Date, string Market,
    int IndexPresentMinutes, int IndexMissingMinutes,
    int FrontFuturePresentMinutes, int FrontFutureMissingMinutes,
    bool PreviousCloseAvailable, int MappedOptionContracts,
    int MappedContractsWithPricePath, int UnmappedContractsWithPricePath,
    int ReceivedOptionChainSnapshots, int OptionQuoteObservations,
    int OptionQuoteObservationsWithOi)
{
    public int RecordedExpiryUniverses { get; init; }
    public int CompleteSelectedExpiryUniverses { get; init; }
    public int SelectedPageContracts { get; init; }
    public int SelectedPageContractsWithPricePath { get; init; }
    public int SelectedPageContractsWithCompleteMinutePath { get; init; }
    public int SelectedPageContractsWithMappedLotMetadata { get; init; }
    public DateTimeOffset? DatedInstrumentMasterCapturedAtUtc { get; init; }
    public string? DatedInstrumentMasterFingerprint { get; init; }
    public int ExecutableBidOfferObservations { get; init; }
    public int ChainLtpObservations { get; init; }
    public int CandleCloseProxyObservations { get; init; }
    public int ChainLtpObservationsWithOi { get; init; }
    public bool ConfirmationFutureRequired { get; init; } = true;
    public bool UnderlyingReady => IndexMissingMinutes == 0 &&
        (!ConfirmationFutureRequired || FrontFutureMissingMinutes == 0) && PreviousCloseAvailable;
}

public static class HistoricalReplayArchiveCoverageEvaluator
{
    public static HistoricalReplayArchiveCoverage Evaluate(
        HistoricalReplayArchiveSlice slice, string market)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var session = slice.Session;
        var index = HistoricalSessionCoverageEvaluator.Evaluate(session.Date,
            CurrentMinuteOpens(session, session.IndexSymbol));
        var future = slice.FrontFutureSymbol is null
            ? new HistoricalIndexMinuteCoverage(375, 0, 375, 375)
            : HistoricalSessionCoverageEvaluator.Evaluate(session.Date,
                CurrentMinuteOpens(session, slice.FrontFutureSymbol));
        var observations = session.Quotes.Where(value =>
            value.AvailableAtUtc >= session.StartUtc &&
            value.AvailableAtUtc < session.EndUtc && value.Symbol != session.IndexSymbol && value.Symbol != slice.FrontFutureSymbol);
        // One chain receipt is a mark, not a price path. Require at least two
        // distinct observation times before counting a contract as replayable.
        var firstTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var pricePathSymbols = new HashSet<string>(StringComparer.Ordinal);
        int observationCount = 0, withOi = 0, sides = 0, chainLtps = 0, proxies = 0, chainOi = 0;
        foreach (var observation in observations)
        {
            observationCount++;
            if (observation.OpenInterest.HasValue) withOi++;
            if (observation.BidPrice is > 0m && observation.OfferPrice is > 0m) sides++;
            if (observation.Source == "GrowwChainReceiptLtp")
            {
                chainLtps++;
                if (observation.OpenInterest.HasValue) chainOi++;
            }
            if (observation.Source == "CandleCloseProxy") proxies++;
            if (!firstTimes.TryAdd(observation.Symbol, observation.AvailableAtUtc) && firstTimes[observation.Symbol] != observation.AvailableAtUtc)
                pricePathSymbols.Add(observation.Symbol);
        }
        var universes = HistoricalUniversePageCoverage.Evaluate(slice.UniversePages);
        var selectedPageSymbols = slice.UniversePages
            .SelectMany(value => value.FetchedSymbols)
            .Select(value => slice.ProviderSymbolToReplaySymbol.TryGetValue(
                value, out var mapped) ? mapped : value)
            .Distinct(StringComparer.Ordinal).ToArray();
        var mappedLotSymbols = slice.OptionContracts.Where(value =>
                value.LotSize > 0 && value.TickSize > 0m)
            .Select(value => value.TradingSymbol)
            .ToHashSet(StringComparer.Ordinal);
        var fullPaths = session.Bars.Where(value => value.IntervalSeconds == 60 &&
                value.OpenTimeUtc >= session.StartUtc &&
                value.OpenTimeUtc < session.EndUtc)
            .GroupBy(value => value.Symbol, StringComparer.Ordinal)
            .Where(group => HistoricalSessionCoverageEvaluator.Evaluate(session.Date,
                group.Select(value => value.OpenTimeUtc)).IsComplete)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return new(session.Date, market, index.PresentMinutes,
            index.MissingMinutes, future.PresentMinutes, future.MissingMinutes,
            slice.PreviousClose.HasValue, slice.OptionContracts.Count,
            slice.OptionContracts.Count(value => pricePathSymbols.Contains(
                value.TradingSymbol)), slice.UnmappedGrowwSymbols.Count,
            session.OptionChains.Count(value => value.AvailableAtUtc >=
                session.StartUtc && value.AvailableAtUtc < session.EndUtc),
            observationCount, withOi)
        {
            ConfirmationFutureRequired = session.IndexSymbol != "BANKNIFTY",
            RecordedExpiryUniverses = universes.Count,
            CompleteSelectedExpiryUniverses = universes.Count(value =>
                value.AllSelectedPagesFetched),
            SelectedPageContracts = selectedPageSymbols.Length,
            SelectedPageContractsWithPricePath = selectedPageSymbols.Count(value =>
                pricePathSymbols.Contains(value)),
            SelectedPageContractsWithCompleteMinutePath = selectedPageSymbols.Count(value =>
                fullPaths.Contains(value)),
            SelectedPageContractsWithMappedLotMetadata = selectedPageSymbols.Count(value =>
                mappedLotSymbols.Contains(value)),
            DatedInstrumentMasterCapturedAtUtc = slice.InstrumentMasterCapturedAtUtc,
            DatedInstrumentMasterFingerprint = slice.InstrumentMasterSnapshotFingerprint,
            ExecutableBidOfferObservations = sides,
            ChainLtpObservations = chainLtps,
            CandleCloseProxyObservations = proxies,
            ChainLtpObservationsWithOi = chainOi
        };
    }

    private static IEnumerable<DateTimeOffset> CurrentMinuteOpens(
        HistoricalReplaySessionData session, string symbol) =>
        session.Bars.Where(value => value.Symbol == symbol &&
                value.IntervalSeconds == 60 && value.OpenTimeUtc >= session.StartUtc &&
                value.OpenTimeUtc < session.EndUtc)
            .Select(value => value.OpenTimeUtc);
}
