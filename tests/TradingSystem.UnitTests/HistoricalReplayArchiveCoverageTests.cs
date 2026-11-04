using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class HistoricalReplayArchiveCoverageTests
{
    [Fact]
    public void RequiresCompleteIndexAndFutureBeforeDeclaringUnderlyingReady()
    {
        var tradingDate = new DateOnly(2026, 10, 29);
        var start = new DateTimeOffset(tradingDate.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var index = Enumerable.Range(0, 375).Select(minute => Bar(start, minute, "NIFTY"));
        var future = Enumerable.Range(0, 375).Select(minute => Bar(start, minute,
            "NIFTY26NOVFUT"));
        var contract = new NiftyOptionContractCandidate(Guid.NewGuid(), "NIFTY22000CE",
            InstrumentType.CallOption, tradingDate.AddDays(5), 22000m, 75, 0.05m);
        var session = new HistoricalReplaySessionData(tradingDate, "NIFTY",
            index.Concat(future).ToArray(), [
                new HistoricalReplayQuote("NIFTY22000CE", start.AddMinutes(20), 150m)
                { OpenInterest = 1000m, BidPrice = 149m, OfferPrice = 151m },
                new HistoricalReplayQuote("NIFTY22000CE", start.AddMinutes(21), 151m),
                new("NSE-NIFTY-03Nov26-22050-CE", start.AddMinutes(20), 125m)
                { Source = "GrowwChainReceiptLtp" }
            ], start, start.AddMinutes(375))
        {
            OptionChains = [new HistoricalOptionChainFrame(start.AddMinutes(20),
                start.AddMinutes(20), tradingDate.AddDays(5),
                [new OptionOpenInterestStrike(22000m, 1000m, 500m)])]
        };
        var slice = new HistoricalReplayArchiveSlice(session, 21900m,
            "NIFTY26NOVFUT", [contract], ["NSE-NIFTY-03Nov26-22050-CE"])
        {
            UniversePages = [new HistoricalUniversePageEvidence(
                tradingDate.AddDays(5), 0, 2, 2, "hash", [
                    "NSE-NIFTY-03Nov26-22000-CE", "NSE-NIFTY-03Nov26-22050-CE"])],
            ProviderSymbolToReplaySymbol = new Dictionary<string, string>
            {
                ["NSE-NIFTY-03Nov26-22000-CE"] = "NIFTY22000CE"
            },
            InstrumentMasterSnapshotFingerprint = new string('A', 64),
            InstrumentMasterCapturedAtUtc = start.AddMinutes(-30)
        };
        var full = HistoricalReplayArchiveCoverageEvaluator.Evaluate(slice, "nifty");
        Assert.True(full.UnderlyingReady);
        Assert.Equal(1, full.MappedContractsWithPricePath);
        Assert.Equal(1, full.UnmappedContractsWithPricePath);
        Assert.Equal(1, full.ReceivedOptionChainSnapshots);
        Assert.Equal(3, full.OptionQuoteObservations);
        Assert.Equal(1, full.OptionQuoteObservationsWithOi);
        Assert.Equal(1, full.RecordedExpiryUniverses);
        Assert.Equal(1, full.CompleteSelectedExpiryUniverses);
        Assert.Equal(2, full.SelectedPageContracts);
        Assert.Equal(1, full.SelectedPageContractsWithPricePath);
        Assert.Equal(0, full.SelectedPageContractsWithCompleteMinutePath);
        Assert.Equal(1, full.SelectedPageContractsWithMappedLotMetadata);
        Assert.Equal(1, full.ExecutableBidOfferObservations);
        Assert.Equal(1, full.ChainLtpObservations);
        Assert.Equal(2, full.CandleCloseProxyObservations);
        Assert.Equal(0, full.ChainLtpObservationsWithOi);
        Assert.Equal(new string('A', 64), full.DatedInstrumentMasterFingerprint);
        Assert.Equal(start.AddMinutes(-30), full.DatedInstrumentMasterCapturedAtUtc);

        var partial = HistoricalReplayArchiveCoverageEvaluator.Evaluate(slice with
        {
            Session = session with
            {
                Bars = session.Bars.Where(value => value.Symbol != "NIFTY26NOVFUT" ||
                    value.OpenTimeUtc != start.AddMinutes(100)).ToArray()
            }
        }, "nifty");
        Assert.False(partial.UnderlyingReady);
        Assert.Equal(1, partial.FrontFutureMissingMinutes);
    }

    [Fact]
    public void ASingleChainReceiptDoesNotClaimMappedOptionPricePath()
    {
        var date = new DateOnly(2026, 10, 29);
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var contract = new NiftyOptionContractCandidate(Guid.NewGuid(),
            "NIFTY22000CE", InstrumentType.CallOption, date.AddDays(5),
            22000m, 75, .05m);
        var session = new HistoricalReplaySessionData(date, "NIFTY", [], [
            new HistoricalReplayQuote("NIFTY22000CE", start.AddMinutes(1), 150m)
            { Source = "GrowwChainReceiptLtp" },
            new HistoricalReplayQuote("NIFTY22000CE", start.AddMinutes(-1), 140m)
            { Source = "GrowwChainReceiptLtp" }
        ], start, start.AddMinutes(375));
        var slice = new HistoricalReplayArchiveSlice(session, null, null,
            [contract], []);

        var coverage = HistoricalReplayArchiveCoverageEvaluator.Evaluate(slice,
            "nifty");
        Assert.Equal(1, coverage.ChainLtpObservations);
        Assert.Equal(1, coverage.OptionQuoteObservations);
        Assert.Equal(0, coverage.MappedContractsWithPricePath);
    }

    private static HistoricalReplayBar Bar(DateTimeOffset start, int minute,
        string symbol) => new(start.AddMinutes(minute), 60, 100m, 101m, 99m, 100m)
    { Symbol = symbol, Volume = 10 };
}
