using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using System.Globalization;

namespace TradingSystem.UnitTests;

public sealed class HistoricalFeedSubscriptionPlannerTests
{
    private static readonly DateOnly Date = new(2026, 11, 4);
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
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
