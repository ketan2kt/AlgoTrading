using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalPremiumProtectionPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29,
        4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SharedProtectionMathTightensOnQuoteAndFillsLater()
    {
        var policy = new HistoricalPremiumProtectionPolicy(new OneEntryPolicy(),
            ProfessionalExitPolicy.For(ProfessionalTradeRegime.Directional), "NSE");
        var result = HistoricalPortfolioReplay.Run([
            new HistoricalReplayBar(Start, 60, 22000m, 22001m,
                21999m, 22000m) { Symbol = "NIFTY" }
        ], [
            Quote(2, 100m), Quote(3, 130m), Quote(4, 110m),
            Quote(5, 109m)
        ], policy, new HistoricalReplayParameters(.10m, 2m, 0m, 0m, 5, 0),
            Start, Start.AddMinutes(6));

        var fill = Assert.Single(result.Trades);
        Assert.Equal("ObservedStop", fill.ExitReason);
        Assert.Equal(Start.AddMinutes(5), fill.ExitAtUtc);
        Assert.Equal(90m, fill.InitialStopPrice);
        Assert.True(fill.FinalStopPrice >= 122.5m);
        Assert.False(policy.ProductionPolicyParityVerified);
    }

    [Fact]
    public void PlanAndExchangeArePartOfPolicyManifest()
    {
        var directional = new HistoricalPremiumProtectionPolicy(
            new OneEntryPolicy(),
            ProfessionalExitPolicy.For(ProfessionalTradeRegime.Directional), "NSE");
        var range = new HistoricalPremiumProtectionPolicy(new OneEntryPolicy(),
            ProfessionalExitPolicy.For(ProfessionalTradeRegime.Range), "NSE");
        var bse = new HistoricalPremiumProtectionPolicy(new OneEntryPolicy(),
            ProfessionalExitPolicy.For(ProfessionalTradeRegime.Directional), "BSE");

        Assert.Equal(directional.CodeVersion, range.CodeVersion);
        Assert.NotEqual(directional.ConfigurationManifest,
            range.ConfigurationManifest);
        Assert.NotEqual(directional.ConfigurationManifest,
            bse.ConfigurationManifest);
    }

    private static HistoricalReplayQuote Quote(int minute, decimal price) =>
        new("NIFTY22000CE", Start.AddMinutes(minute), price);

    private sealed class OneEntryPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "one-entry-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    CodeVersion, .8m, 75)] : [];
    }
}
