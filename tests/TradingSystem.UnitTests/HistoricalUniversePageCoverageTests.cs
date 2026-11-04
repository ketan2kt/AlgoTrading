using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalUniversePageCoverageTests
{
    private static readonly DateOnly Expiry = new(2026, 11, 3);

    [Fact]
    public void CompleteOnlyWhenEveryConsistentPageIsPresent()
    {
        var pages = new[]
        {
            Page(0, ["CE", "PE"], 3),
            Page(1, ["CE2"], 3)
        };
        var complete = Assert.Single(HistoricalUniversePageCoverage.Evaluate(pages));
        Assert.True(complete.AllSelectedPagesFetched);
        Assert.Equal(2, complete.ExpectedPages);

        Assert.False(Assert.Single(HistoricalUniversePageCoverage.Evaluate(
            pages[..1])).AllSelectedPagesFetched);
        Assert.False(Assert.Single(HistoricalUniversePageCoverage.Evaluate(
            [pages[0], pages[1] with { UniverseFingerprint = "revised" }]))
            .AllSelectedPagesFetched);
        Assert.False(Assert.Single(HistoricalUniversePageCoverage.Evaluate(
            [pages[0], pages[1] with { FetchedSymbols = ["CE"] }]))
            .AllSelectedPagesFetched);
        Assert.False(Assert.Single(HistoricalUniversePageCoverage.Evaluate(
            [pages[0], pages[0]])).AllSelectedPagesFetched);
        Assert.False(Assert.Single(HistoricalUniversePageCoverage.Evaluate([
            Page(0, [], 0)])).AllSelectedPagesFetched);
    }

    private static HistoricalUniversePageEvidence Page(int page,
        IReadOnlyList<string> symbols, int total) =>
        new(Expiry, page, 2, total, "hash", symbols);
}
