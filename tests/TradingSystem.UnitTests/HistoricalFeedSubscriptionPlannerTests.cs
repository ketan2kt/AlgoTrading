using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using System.Globalization;

namespace TradingSystem.UnitTests;

public sealed class HistoricalFeedSubscriptionPlannerTests
{
    private static readonly DateOnly Date = new(2026, 11, 4);
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    private static readonly int[] StrikeCounts = [245, 198, 371];
    private static readonly InstrumentType[] OptionTypes = [InstrumentType.CallOption, InstrumentType.PutOption];
    [Fact]
    public void NormalUniverseCapturesAllStrikesOfSelectedExpiryAndSkipsSameDayPairs()
    {
        var source = Instruments();
        var plan = HistoricalFeedSubscriptionPlanner.Select(Date, source);
        Assert.Equal(11, plan.Count); // Three indices, two confirmation futures, six normal option contracts.
        Assert.Equal(6, plan.Count(value => value.Type is InstrumentType.CallOption or InstrumentType.PutOption));
        Assert.DoesNotContain(plan, value => value.ExpiryDate == Date && value.Type != InstrumentType.Future);
        Assert.DoesNotContain(plan, value => value.TradingSymbol.EndsWith("LATER", StringComparison.Ordinal));
        Assert.Equal(plan, HistoricalFeedSubscriptionPlanner.Select(Date, source.Reverse().ToArray()));
    }
    [Fact]
    public void InstrumentLimitNeverSilentlyTruncatesTheResearchUniverse()
    {
        Assert.Throws<InvalidOperationException>(() => HistoricalFeedSubscriptionPlanner.Select(Date, Instruments(), 10));
        var rows = Instruments();
        Assert.Throws<InvalidOperationException>(() => HistoricalFeedSubscriptionPlanner.Select(Date, rows.Append(rows[0]).ToArray()));
    }

    [Fact]
    public void ProductionSizedUniverseFitsBudgetWithSymmetricPreSessionBandsForEveryMarket()
    {
        var basic = Instruments().Where(row => row.Type is InstrumentType.Index or InstrumentType.Future).ToList();
        var anchors = Markets.ToDictionary(market => market, _ => 22000m);
        foreach (var (market, number) in Markets.Select((market, number) => (market, number)))
        foreach (var strike in Enumerable.Range(0, StrikeCounts[number]))
        foreach (var type in OptionTypes)
            basic.Add(new(market, market == "SENSEX" ? "BSE" : "NSE", "FNO",
                (10000 + number * 10000 + strike * 2 + (int)type).ToString(CultureInfo.InvariantCulture),
                market + strike.ToString(CultureInfo.InvariantCulture) + type, type, Date.AddDays(7),
                12000m + strike * 50m, 30, .05m));
        Assert.Equal(1633, basic.Count - 1); // Bank confirmation future is deliberately not selected.
        var plan = HistoricalFeedSubscriptionPlanner.Select(Date, basic, 1000, anchors);
        Assert.Equal(995, plan.Count);
        Assert.Equal(plan, HistoricalFeedSubscriptionPlanner.Select(Date, basic.AsEnumerable().Reverse().ToArray(), 1000, anchors));
        foreach (var market in Markets)
        {
            var options = plan.Where(row => row.Market == market && row.StrikePrice.HasValue).ToArray();
            Assert.Equal(330, options.Length);
            Assert.All(options.GroupBy(row => row.StrikePrice), group => Assert.Equal(2, group.Count()));
            Assert.Equal(50m * 164, options.Max(row => row.StrikePrice) - options.Min(row => row.StrikePrice));
        }
        Assert.Throws<InvalidOperationException>(() => HistoricalFeedSubscriptionPlanner.Select(Date, basic, 1000));
        Assert.Throws<InvalidOperationException>(() => HistoricalFeedSubscriptionPlanner.Select(Date,
            basic.Select(row => row.Type == InstrumentType.CallOption ? row with { StrikePrice = null } : row).ToArray(), 1000, anchors));
    }

    [Fact]
    public void BoundedUniverseIsOnlyCertifiedInsideItsRecordedGuardStrikes()
    {
        var at = new DateTimeOffset(Date.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5));
        var contracts = Enumerable.Range(0, 7).SelectMany(strike => OptionTypes
            .Select(type => new NiftyOptionContractCandidate(Guid.NewGuid(), "NIFTY" + strike + type, type,
                Date.AddDays(7), 21900m + strike * 50, 30, .05m))).ToArray();
        var captured = contracts.Where(row => row.StrikePrice is >= 21950 and <= 22150).Select((row, i) =>
            new HistoricalFeedInstrument("NIFTY", "NSE", "FNO", i.ToString(CultureInfo.InvariantCulture), row.TradingSymbol,
                row.Type, row.ExpiryDate, row.StrikePrice, row.LotSize, row.TickSize)).ToArray();
        var manifest = new HistoricalFeedCaptureManifest(Date, at, at, at.AddHours(7), "test", 2, false, captured)
            { UniverseScope = "PreSessionStrikeBand" };
        var bar = new HistoricalReplayBar(at, 60, 22000, 22100, 21975, 22050) { Symbol = "NIFTY" };
        Assert.True(HistoricalFeedCaptureEvidence.CoversNormalUniverse(manifest, "NIFTY", contracts, Date, [bar]));
        Assert.False(HistoricalFeedCaptureEvidence.CoversNormalUniverse(manifest, "NIFTY", contracts, Date));
        Assert.False(HistoricalFeedCaptureEvidence.CoversNormalUniverse(manifest, "NIFTY", contracts, Date, [bar with { High = 22150 }]));
        Assert.False(HistoricalFeedCaptureEvidence.CoversNormalUniverse(manifest with { Instruments = captured.Skip(1).ToArray() }, "NIFTY", contracts, Date, [bar]));
        Assert.False(HistoricalFeedCaptureEvidence.CoversWindow(manifest with { HasGaps = true }, Date, at, at.AddHours(6)));
    }
    private static HistoricalFeedInstrument[] Instruments() => Markets.SelectMany((market, number) =>
    {
        var exchange = market == "SENSEX" ? "BSE" : "NSE";
        HistoricalFeedInstrument Row(string suffix, InstrumentType type, DateOnly? expiry, int token) =>
            new(market, exchange, type == InstrumentType.Index ? "CASH" : "FNO", (number * 100 + token).ToString(CultureInfo.InvariantCulture),
                market + suffix, type, expiry, type is InstrumentType.CallOption or InstrumentType.PutOption ? 22000 : null,
                type == InstrumentType.Index ? 1 : 30, type == InstrumentType.Index ? 0 : .05m);
        return new[] { Row("", InstrumentType.Index, null, 1), Row("FUT", InstrumentType.Future, Date.AddDays(20), 2),
            Row("TODAYCE", InstrumentType.CallOption, Date, 3), Row("TODAYPE", InstrumentType.PutOption, Date, 4),
            Row("CE", InstrumentType.CallOption, Date.AddDays(7), 5), Row("PE", InstrumentType.PutOption, Date.AddDays(7), 6),
            Row("LATER", InstrumentType.CallOption, Date.AddDays(14), 7) };
    }).ToArray();
}
