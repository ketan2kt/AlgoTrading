namespace TradingSystem.Application.Execution;

public sealed record HistoricalUniversePageEvidence(DateOnly ExpiryDate, int Page,
    int PageSize, int TotalSelectedContracts, string UniverseFingerprint,
    IReadOnlyList<string> FetchedSymbols);

public sealed record HistoricalUniverseExpiryCoverage(DateOnly ExpiryDate,
    int ExpectedPages, int CompletedPages, int SelectedContracts,
    bool AllSelectedPagesFetched);

public static class HistoricalUniversePageCoverage
{
    public static IReadOnlyList<HistoricalUniverseExpiryCoverage> Evaluate(
        IReadOnlyList<HistoricalUniversePageEvidence> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return pages.GroupBy(value => value.ExpiryDate).OrderBy(value => value.Key)
            .Select(group =>
            {
                var first = group.First();
                var expected = first.PageSize is >= 1 and <= 20 &&
                    first.TotalSelectedContracts >= 0
                    ? Math.Max(1, (first.TotalSelectedContracts + first.PageSize - 1) /
                        first.PageSize) : 0;
                var consistent = expected > 0 &&
                    !string.IsNullOrWhiteSpace(first.UniverseFingerprint) &&
                    group.All(value => value.PageSize == first.PageSize &&
                        value.TotalSelectedContracts == first.TotalSelectedContracts &&
                        value.UniverseFingerprint == first.UniverseFingerprint &&
                        value.Page >= 0 && value.Page < expected &&
                        value.FetchedSymbols is not null &&
                        value.FetchedSymbols.All(symbol =>
                            !string.IsNullOrWhiteSpace(symbol)) &&
                        value.FetchedSymbols.Count == Math.Min(first.PageSize,
                            Math.Max(0, first.TotalSelectedContracts -
                                value.Page * first.PageSize))) &&
                    group.Select(value => value.Page).Distinct().Count() == group.Count() &&
                    group.SelectMany(value => value.FetchedSymbols)
                        .Distinct(StringComparer.Ordinal).Count() ==
                        first.TotalSelectedContracts;
                return new HistoricalUniverseExpiryCoverage(group.Key, expected,
                    group.Count(), first.TotalSelectedContracts,
                    consistent && first.TotalSelectedContracts > 0 &&
                    group.Count() == expected &&
                    Enumerable.Range(0, expected).All(page =>
                        group.Any(value => value.Page == page)));
            }).ToArray();
    }
}
