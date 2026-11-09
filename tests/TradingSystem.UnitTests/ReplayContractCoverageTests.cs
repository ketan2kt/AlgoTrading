using System.Globalization;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class ReplayContractCoverageTests
{
    private static readonly DateOnly Date = new(2026, 11, 10);
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    private static readonly InstrumentType[] Types = [InstrumentType.CallOption, InstrumentType.PutOption];

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public void RecordingAndReplayUseSameExpiryAndEveryRegimeSelectionAsProduction(string market)
    {
        var master = Master(market);
        foreach (var direction in new[] { Direction.Buy, Direction.Sell })
        foreach (var price in new[] { 21974m, 22000m, 22026m })
        {
            var required = RegularOptionContractSelector.RequiredForRange(master, market, Date, price, price);
            var actual = market switch
            {
                "NIFTY" => NiftyOptionContractSelector.Select(master, direction, price, Date,
                    new NiftyOptionSelectionOptions(NormalInTheMoneySteps: 1)),
                "SENSEX" => RegularOptionContractSelector.Select(master, market, Date, direction, price, 1),
                _ => HistoricalBankNiftyOptionSelector.Select(master, Date, direction, price)
            };
            Assert.NotNull(actual);
            Assert.Equal(Date.AddDays(7), actual.ExpiryDate);
            Assert.Contains(actual, required);
        }
    }

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public void UnrecordedStrikesAndMetadataAreRejectedInsteadOfAssumedFills(string market)
    {
        var master = Master(market);
        var captured = Rows(market, master.Where(c => c.ExpiryDate > Date && c.StrikePrice is >= 21900 and <= 22100));
        Assert.Empty(ReplayContractCoverage.CheckRange(master, captured, market, Date, 22000, 22000, new()));
        Assert.NotEmpty(ReplayContractCoverage.CheckRange(master, captured, market, Date, 22000, 22500, new()));
        Assert.NotEmpty(ReplayContractCoverage.CheckRange(master, captured.Select(c => c with { LotSize = 0 }).ToArray(),
            market, Date, 22000, 22000, new()));
    }

    [Fact]
    public void ModifiedItmSettingsAreCheckedEvenWhenSpotRemainsInsideTheRecordedBand()
    {
        var master = Master("NIFTY");
        var captured = Rows("NIFTY", master.Where(c => c.ExpiryDate > Date && c.StrikePrice is >= 21900 and <= 22100));
        var slice = Slice(master, captured);
        Assert.Empty(ReplayContractCoverage.ValidateReplay(slice, Configuration()));
        var changed = Configuration() with { NiftySelection = new(NormalInTheMoneySteps: 5) };
        Assert.Contains(ReplayContractCoverage.ValidateReplay(slice, changed), r => r.Contains("Unsupported contract coverage", StringComparison.Ordinal));
        Assert.NotEmpty(ReplayContractCoverage.ValidateReplay(slice with { RecordedContractUniverse = null }, Configuration()));
        Assert.NotEmpty(ReplayContractCoverage.ValidateReplay(slice with {
            RecordedContractUniverse = new("unknown-future-selector", captured) }, Configuration()));
    }

    [Fact]
    public void PreOpenCheckSavesAllThreeBoundsBudgetAndPolicyAndCannotBeRetimedAfterOpen()
    {
        var master = Markets.SelectMany(Master).ToArray();
        var plan = Markets.SelectMany(m => Rows(m, master.Where(c => c.TradingSymbol.StartsWith(m, StringComparison.Ordinal) && c.ExpiryDate > Date))).ToArray();
        var at = new DateTimeOffset(Date.ToDateTime(new TimeOnly(9, 5)), TimeSpan.FromHours(5.5));
        var receipt = SessionPolicyReceipt.Create(Date, at, Guid.NewGuid().ToString("N"), Configuration());
        var input = new ReplayContractCoverageInput(master, Markets.ToDictionary(m => m, _ => 22000m));
        var result = ReplayContractCoverage.Preflight(Date, at, plan, input, receipt);
        Assert.True(result.Ready);
        Assert.Equal(3, result.Markets.Count);
        Assert.All(result.Markets, m => { Assert.NotNull(m.SupportedUnderlyingLow); Assert.NotNull(m.SupportedUnderlyingHigh); });
        Assert.Equal(plan.Length * 2, result.Subjects);
        Assert.False(ReplayContractCoverage.Preflight(Date, at.AddMinutes(11), plan, input, receipt).Ready);
        Assert.False(ReplayContractCoverage.Preflight(Date, at, plan, input, null).Ready);
        Assert.False(ReplayContractCoverage.Preflight(Date, at, plan.Where(c => c.Market != "BANKNIFTY").ToArray(), input, receipt).Ready);
        Assert.False(ReplayContractCoverage.Preflight(Date, at, plan, input,
            receipt with { Settings = receipt.Settings with { NiftySelection = new(NormalInTheMoneySteps: 100) } }).Ready);
    }

    [Fact]
    public void RangeProofIncludesIntermediateStrikesNotOnlyEndpoints()
    {
        var master = Master("NIFTY");
        var captured = Rows("NIFTY", master.Where(c => c.ExpiryDate > Date && c.StrikePrice != 22000));
        Assert.NotEmpty(ReplayContractCoverage.CheckRange(master, captured, "NIFTY", Date, 21900, 22100, new()));
    }

    [Fact]
    public void AChangedCompiledSelectorCannotSilentlySkipAnUnrecordedCandidate()
    {
        var at = DateTimeOffset.UnixEpoch;
        var frame = new HistoricalReplayPortfolioFrame(new(at, [], new Dictionary<string, HistoricalReplayQuote>()), null, []);
        var guard = new HistoricalContractCoveragePolicy(new OutsideUniversePolicy(), "NIFTY", Date,
            new HashSet<string>(StringComparer.Ordinal) { "NIFTY-RECORDED-CE" });
        var error = Assert.Throws<UnsupportedReplayContractException>(() => guard.Evaluate(frame));
        Assert.Contains("NIFTY-UNRECORDED-CE", error.Message, StringComparison.Ordinal);
        Assert.Contains("2026-11-10", error.Message, StringComparison.Ordinal);
    }

    private sealed class OutsideUniversePolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "test-changed-selector";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) =>
            [new("NIFTY-UNRECORDED-CE", frame.Market.AvailableAtUtc, "test", 1, 30)];
    }

    private static HistoricalReplayArchiveSlice Slice(NiftyOptionContractCandidate[] master, HistoricalFeedInstrument[] captured)
    {
        var at = new DateTimeOffset(Date.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5));
        return new(new(Date, "NIFTY", [new(at, 60, 22000, 22001, 21999, 22000) { Symbol = "NIFTY" }], [], at, at.AddHours(6.25)),
            22000, "NIFTY-FUT", master, []) { RecordedContractUniverse = new(RegularOptionContractSelector.Version, captured) };
    }

    private static NiftyOptionContractCandidate[] Master(string market) => Enumerable.Range(0, 41).SelectMany(i => Types.SelectMany(type =>
        new[] { Date, Date.AddDays(7), Date.AddDays(30) }.Select(expiry => new NiftyOptionContractCandidate(Guid.NewGuid(),
            FormattableString.Invariant($"{market}-{expiry:yyyyMMdd}-{21000 + i * 50}-{type}"), type, expiry, 21000 + i * 50, 30, .05m)))).ToArray();

    private static HistoricalFeedInstrument[] Rows(string market, IEnumerable<NiftyOptionContractCandidate> contracts) => contracts
        .Where(c => c.ExpiryDate == Date.AddDays(7)).Select((c, i) => new HistoricalFeedInstrument(market,
            market == "SENSEX" ? "BSE" : "NSE", "FNO", i.ToString(CultureInfo.InvariantCulture), c.TradingSymbol,
            c.Type, c.ExpiryDate, c.StrikePrice, c.LotSize, c.TickSize)).ToArray();

    private static HistoricalNormalPolicyConfiguration Configuration() => new(60, 4, 2, new(9, 30), 5, 5, 10m, new(),
        new(true, 1.5m, 500m, 100m, 100m, 10m, 1m, 8, 100_000m, 1_000_000m, 100_000m,
            new(9, 15), new(15, 0), new(15, 15), 5, 5, true, .35m, 1m, .6m),
        new(new(15, 15), true, true, true, true, .6m, true, .55m, 3, true, .8m, 5, 5, 60),
        new(new(9, 15), new(15, 0), new(15, 15), 5, 60, 10m, 5),
        new(new(15, 15), true, true, true, .55m, 3, true, .8m, 5, 5, 60),
        new MarketRegimeOptions(), new PriceActionStrategyOptions(), new OpeningRangeBreakoutOptions());
}
