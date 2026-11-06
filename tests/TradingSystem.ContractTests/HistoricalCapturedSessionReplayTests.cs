using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.ContractTests;

// Synthetic full-session pipeline fixture, NOT historical performance evidence.
// Uses the real accumulator, compressed archive, disk-backed replay path,
// coverage selector and portfolio execution engine, without broker access.
public sealed class HistoricalCapturedSessionReplayTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("sarthi-captured-session-").FullName;
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    private static readonly DateOnly Date = new(2026, 11, 6);
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 9, 15, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public async Task WholeSessionForThreeMarketsSurvivesArchiveRestartAndReplaysCounterfactualTrades()
    {
        using (var archive = Archive())
        {
            var accumulator = new GrowwHistoricalFeedAccumulator(archive);
            var instruments = Markets.Select((market, i) => new HistoricalFeedInstrument(market,
                market == "SENSEX" ? "BSE" : "NSE", "FNO", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                market + "CE", InstrumentType.CallOption, Date.AddDays(7), 22000, 30, .05m)).ToArray();
            for (var seconds = 0; seconds <= 375 * 60; seconds += 2)
            {
                var at = Start.AddSeconds(seconds);
                var price = 100m + seconds / 300m;
                foreach (var instrument in instruments)
                {
                    accumulator.Observe(instrument, "price", new(at, price, 10000 + seconds, 50000, null, null, null, null), at);
                    accumulator.Observe(instrument, "depth", new(at, null, null, null, price - .1m, 300, price + .1m, 300), at);
                }
                accumulator.Sample(at);
                if (seconds % 128 == 0) await archive.FlushBatchAsync(CancellationToken.None);
            }
            await archive.StopAsync(CancellationToken.None);
            Assert.False(archive.ReadHealth(Date).GapObserved);
        }
        using var restarted = Archive();
        var sessions = new List<HistoricalReplaySessionData>();
        foreach (var market in Markets)
        {
            var symbol = market + "CE";
            var path = await restarted.ReadReplayAsync(Date, market, market == "SENSEX" ? "BSE" : "NSE",
                new Dictionary<string,string> { [symbol] = symbol }, Start, Start.AddMinutes(375), CancellationToken.None, streamingOnly: true);
            Assert.False(path.HasCaptureGaps);
            Assert.Equal(11251, path.Quotes.Count);
            Assert.IsAssignableFrom<IHistoricalChronologicalQuotePath>(path.Quotes);
            var bars = Enumerable.Range(0, 375).Select(minute => new HistoricalReplayBar(Start.AddMinutes(minute), 60,
                22000 + minute, 22001 + minute, 21999 + minute, 22000 + minute) { Symbol = market }).ToArray();
            sessions.Add(new(Date, market, bars, path.Quotes, Start, Start.AddMinutes(375))
            {
                ExchangeCode = market == "SENSEX" ? "BSE" : "NSE", RequiredInputsComplete = true,
                EligibleOptionUniverseComplete = true, StrategyInputsComplete = true, ExecutableQuoteSidesComplete = true,
                HistoricalChargesVerified = false, ExecutionOptionSymbols = [symbol], PolicyInputManifest = "Synthetic-full-session-fixture"
            });
        }
        var cohort = HistoricalReplayCohortSelector.Select(sessions, Markets, allowModeledHistoricalCosts: true);
        Assert.Empty(cohort.ExcludedDates);
        Assert.Equal(3, cohort.IncludedSessionCount);
        foreach (var session in sessions)
        {
            var slice = new HistoricalReplayArchiveSlice(session, 21900, null, [], []);
            var audit = OperationalAutomationService.BuildReplayInputReport(slice, session.IndexSymbol, Start.AddHours(7));
            Assert.False(audit.ActionRequired);
            Assert.Equal("modeled", audit.Metrics["charges"]);
            var missing = OperationalAutomationService.BuildReplayInputReport(slice with
            {
                Session = session with { EligibleOptionUniverseComplete = false }
            }, session.IndexSymbol, Start.AddHours(7));
            Assert.True(missing.ActionRequired);
            Assert.Contains(missing.Findings, value => value.Contains("option universe", StringComparison.Ordinal));
            var controlGap = OperationalAutomationService.BuildReplayInputReport(slice with
            {
                ControlStateGaps = ["Missing pre-open configuration receipt"]
            }, session.IndexSymbol, Start.AddHours(7));
            Assert.True(controlGap.ActionRequired);
        }
        var result = HistoricalPortfolioReplay.Run(sessions.SelectMany(session => session.Bars).ToArray(),
            HistoricalQuotePaths.Merge(sessions.Select(session => session.Quotes)), new Policy(),
            SessionPolicyReceipt.ComparisonExecution, Start, Start.AddMinutes(375),
            contractExchanges: Markets.ToDictionary(market => market + "CE", market => market == "SENSEX" ? "BSE" : "NSE"));
        Assert.Equal(3, result.CompletedPositions.Count);
        Assert.Equal(0, result.MissingEntryQuoteCount);
        Assert.Equal(0, result.MissingExitQuoteCount);
        Assert.Equal(0, result.StaleQuoteCount);
        Assert.All(result.Trades, fill => { Assert.Equal("ObservedOffer", fill.EntryPriceSource); Assert.Equal("ObservedBid", fill.ExitPriceSource); });
        Assert.All(Markets, market => Assert.Contains(result.Trades, fill => fill.Symbol == market + "CE"));
    }

    [Fact]
    public async Task FullSubscriptionBurstIsBufferedAndFlushedWithoutDroppingAnyInstrument()
    {
        using var archive = Archive();
        var accumulator = new GrowwHistoricalFeedAccumulator(archive);
        for (var i = 0; i < 995; i++)
        {
            var market = Markets[i % Markets.Length];
            var instrument = new HistoricalFeedInstrument(market, market == "SENSEX" ? "BSE" : "NSE", "FNO",
                i.ToString(System.Globalization.CultureInfo.InvariantCulture), market + i + "CE", InstrumentType.CallOption,
                Date.AddDays(7), 22000 + i, 30, .05m);
            accumulator.Observe(instrument, "price", new(Start, 100, 10000, 50000, null, null, null, null), Start);
            accumulator.Observe(instrument, "depth", new(Start, null, null, null, 99, 300, 101, 300), Start);
        }
        for (var sample = 0; sample < 10; sample++) accumulator.Sample(Start.AddSeconds(sample * 2));
        await archive.StopAsync(CancellationToken.None);
        var restored = await archive.ReadAsync(Date, CancellationToken.None);
        Assert.Equal(9950, restored.Observations.Count);
        Assert.False(restored.HasCaptureGaps);
        Assert.Equal(995, restored.Observations.Select(row => row.Request.TradingSymbol).Distinct().Count());
    }

    private HistoricalQuoteArchive Archive() => new(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance, directory);
    private sealed class Policy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "synthetic-capture-pipeline-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1) ? Markets.Select(market =>
                new HistoricalReplayCandidate(market + "CE", frame.Market.AvailableAtUtc, CodeVersion, .8m, 30) { LotSize = 30 }).ToArray() : [];
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
