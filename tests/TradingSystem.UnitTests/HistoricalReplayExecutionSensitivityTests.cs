using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalReplayExecutionSensitivityTests
{
    [Fact]
    public void SlippageStressKeepsFrozenInputsAndDoesNotClaimVerdictOnMissingEvidence()
    {
        var session = Session();
        var parameters = new HistoricalReplayParameters(.10m, .15m, .001m,
            .001m, 5, 0);

        var result = HistoricalReplayExecutionSensitivity.Run([session],
            _ => new OneEntryPolicy(), _ => new OneEntryPolicy(),
            parameters, parameters);

        Assert.Equal([1m, 2m, 3m], result.Scenarios.Select(value =>
            value.SlippageMultiplier).ToArray());
        Assert.Single(result.Scenarios.Select(value =>
            value.Comparison.DataFingerprint).Distinct());
        Assert.Equal(3, result.Scenarios.Select(value =>
            value.Comparison.RunFingerprint).Distinct().Count());
        Assert.True(result.Scenarios[0].Comparison.BaselineNet >
            result.Scenarios[2].Comparison.BaselineNet);
        Assert.False(result.HasCredibleVerdict);
        Assert.False(result.ValidationImprovesAcrossScenarios);
    }

    [Fact]
    public void InvalidStressedSlippageIsRejectedRatherThanClamped()
    {
        var parameters = new HistoricalReplayParameters(.10m, .15m,
            .02m, .02m, 5, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HistoricalReplayExecutionSensitivity.Run([Session()],
                _ => new OneEntryPolicy(), _ => new OneEntryPolicy(),
                parameters, parameters));
    }

    [Fact]
    public void ChangingPolicyConfigurationAcrossStressScenariosIsRejected()
    {
        var sequence = 0;
        var parameters = new HistoricalReplayParameters(.10m, .15m,
            .001m, .001m, 5, 0);
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalReplayExecutionSensitivity.Run([Session()],
                _ => new OneEntryPolicy($"sequence={++sequence}"),
                _ => new OneEntryPolicy("stable"), parameters, parameters));
    }

    private static HistoricalReplaySessionData Session()
    {
        var date = new DateOnly(2026, 10, 29);
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        return new HistoricalReplaySessionData(date, "NIFTY", [
            new HistoricalReplayBar(start, 60, 100m, 101m, 99m, 100m)
            { Symbol = "NIFTY" }
        ], [
            new HistoricalReplayQuote("NIFTY22000CE", start.AddMinutes(2), 100m),
            new HistoricalReplayQuote("NIFTY22000CE", start.AddMinutes(3), 120m),
            new HistoricalReplayQuote("NIFTY22000CE", start.AddMinutes(4), 115m)
        ], start, start.AddMinutes(5));
    }

    private sealed class OneEntryPolicy(string configuration = "stable")
        : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "one-entry-test-v1";
        public string ConfigurationManifest => configuration;
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.CompletedBars.Count == 1 &&
            frame.OpenPosition is null && frame.ClosedTrades.Count == 0
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    CodeVersion, .8m, 75)] : [];
    }
}
