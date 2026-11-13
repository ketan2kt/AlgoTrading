using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class ReplayDepthMonitorTests
{
    private static readonly DateTimeOffset Open = new(2026, 11, 13, 3, 45, 0, TimeSpan.Zero);

    [Fact]
    public void DetectsAndRetainsSensexOutageEvenWhenOtherMarketsAndGlobalWritesContinue()
    {
        var monitor = new ReplayDepthMonitor(Open, Open.AddMinutes(375));
        Assert.False(monitor.Check(Open.AddSeconds(-1), null));
        Assert.False(monitor.Check(Open.AddSeconds(30), Health(30, 0, 30)));
        Assert.True(monitor.Check(Open.AddSeconds(31), Health(31, 0, 31)));
        Assert.Equal(["SENSEX"], monitor.Snapshot().Where(x => x.Gaps.Count != 0).Select(x => x.Market));
        Assert.True(monitor.Check(Open.AddSeconds(32), Health(32, 32, 32)));
        var gap = Assert.Single(monitor.Snapshot().Single(x => x.Market == "SENSEX").Gaps);
        Assert.Equal(Open, gap.StartUtc);
        Assert.Equal(Open.AddSeconds(32), gap.EndUtc);
        Assert.True(monitor.Check(Open.AddSeconds(33), Health(33, 33, 33)));
        Assert.Equal(gap, Assert.Single(monitor.Snapshot().Single(x => x.Market == "SENSEX").Gaps));
    }

    [Fact]
    public void ClosingAuditDoesNotLetAfterHoursSamplesMaskMissingEndOfSession()
    {
        var close = Open.AddMinutes(1);
        var monitor = new ReplayDepthMonitor(Open, close);
        Assert.False(monitor.Check(Open.AddSeconds(20), Health(20, 20, 20)));
        Assert.True(monitor.Check(close, Health(60, 61, 60)));
        Assert.False(monitor.Check(close.AddMinutes(1), Health(60, 60, 60)));
        var gap = Assert.Single(monitor.Snapshot().Single(x => x.Market == "SENSEX").Gaps);
        Assert.Equal(Open.AddSeconds(20), gap.StartUtc);
        Assert.Equal(close, gap.EndUtc);
    }

    [Fact]
    public void LiveGapEvidenceSurvivesReconnectButDoesNotCarryIntoNextSession()
    {
        var pulse = new ResearchCapturePulse();
        var day = DateOnly.FromDateTime(Open.DateTime);
        pulse.Starting(day);
        pulse.OptionDepthAudit([new("SENSEX", null, null, [new HistoricalDepthGap(Open, Open.AddMinutes(1))])]);
        pulse.Starting(day);
        Assert.Single(pulse.Read(day, true).OptionDepthAudit);
        pulse.Starting(day.AddDays(1));
        Assert.Empty(pulse.Read(day.AddDays(1), true).OptionDepthAudit);
    }

    private static HistoricalQuoteArchiveHealth Health(int nifty, int sensex, int bank)
    {
        var receipts = new Dictionary<string, DateTimeOffset> {
            ["NIFTY"] = Open.AddSeconds(nifty), ["SENSEX"] = Open.AddSeconds(sensex),
            ["BANKNIFTY"] = Open.AddSeconds(bank)
        };
        return new(0, false, receipts["NIFTY"], null)
        {
            LastOptionWrittenAtByMarket = receipts,
            LastOptionSourceAtByMarket = receipts
        };
    }
}
