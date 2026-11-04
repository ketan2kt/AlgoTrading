using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class HistoricalBankNiftySignalPolicyTests
{
    [Fact]
    public void DeferredBankOptionAdmissionUsesLaterTwoSidedQuoteAndFrozenProtection()
    {
        var date = new DateOnly(2026, 10, 29);
        var contract = Contract("BANKNIFTY-NOV-55000-CE", InstrumentType.CallOption,
            new DateOnly(2026, 11, 26), 55000m);
        var policy = new HistoricalBankNiftySignalPolicy(Guid.Empty, date, 60,
            new TimeOnly(9, 15), new TimeOnly(15, 0), 2, [contract]);
        var at = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.FromHours(5.5));
        var pending = new HistoricalReplayCandidate(contract.TradingSymbol, at,
            "Bank index directional breakout", .75m, 60)
        { RequiresDeferredQuoteAdmission = true };
        var frame = new HistoricalReplayPortfolioFrame(
            new HistoricalReplayFrame(at, [], new Dictionary<string, HistoricalReplayQuote>()),
            null, []);

        Assert.Null(policy.AssessDeferredEntry(frame, pending,
            new HistoricalReplayQuote(contract.TradingSymbol, at, 100m)
            { BidPrice = 99.5m, OfferPrice = 100m }));
        Assert.Null(policy.AssessDeferredEntry(frame, pending,
            new HistoricalReplayQuote(contract.TradingSymbol, at.AddSeconds(2), 100m)
            { BidPrice = 97m, OfferPrice = 100m }));
        Assert.Null(policy.AssessDeferredEntry(frame, pending,
            new HistoricalReplayQuote(contract.TradingSymbol, at.AddSeconds(2), 100m)));
        Assert.Null(policy.AssessDeferredEntry(frame, pending,
            new HistoricalReplayQuote(contract.TradingSymbol, at.AddSeconds(2), 100m)
            { BidPrice = 101m, OfferPrice = 100m }));

        var admitted = policy.AssessDeferredEntry(frame with { Market = frame.Market with { AvailableAtUtc = at.AddSeconds(2) } }, pending,
            new HistoricalReplayQuote(contract.TradingSymbol, at.AddSeconds(2), 100m)
            { BidPrice = 99.5m, OfferPrice = 100m });
        Assert.NotNull(admitted);
        Assert.Equal(at.AddSeconds(2), admitted.DecidedAtUtc);
        Assert.Equal(90m, admitted.PlannedStopPrice);
        Assert.Equal(110m, admitted.PlannedTargetPrice);
        Assert.False(admitted.RequiresDeferredQuoteAdmission);
    }

    [Fact]
    public void PolicyManifestIsOrderIndependentButCapturesLotAndContractChanges()
    {
        var date = new DateOnly(2026, 10, 29);
        var contracts = new[]
        {
            Contract("BANKNIFTY-NOV-55000-CE", InstrumentType.CallOption,
                new DateOnly(2026, 11, 26), 55000m),
            Contract("BANKNIFTY-NOV-55000-PE", InstrumentType.PutOption,
                new DateOnly(2026, 11, 26), 55000m)
        };
        HistoricalBankNiftySignalPolicy Policy(int lots,
            IReadOnlyList<NiftyOptionContractCandidate> items) =>
            new(Guid.Empty, date, 300, new TimeOnly(9, 15),
                new TimeOnly(15, 0), lots, items);

        var original = Policy(2, contracts).ConfigurationManifest;
        Assert.Equal(original, Policy(2, contracts.Reverse().ToArray())
            .ConfigurationManifest);
        Assert.NotEqual(original, Policy(3, contracts).ConfigurationManifest);
        Assert.NotEqual(original, Policy(2, [contracts[0] with
        { LotSize = 15 }, contracts[1]]).ConfigurationManifest);
    }

    [Fact]
    public void SelectsNextMonthlyExpiryAndNearestValidStrike()
    {
        var day = new DateOnly(2026, 10, 29);
        var contracts = new[]
        {
            Contract("BANKNIFTY-TODAY-CE", InstrumentType.CallOption, day, 55000m),
            Contract("BANKNIFTY-NOV-55100-CE", InstrumentType.CallOption,
                new DateOnly(2026, 11, 26), 55100m),
            Contract("BANKNIFTY-NOV-55000-CE", InstrumentType.CallOption,
                new DateOnly(2026, 11, 26), 55000m),
            Contract("BANKNIFTY-DEC-55000-CE", InstrumentType.CallOption,
                new DateOnly(2026, 12, 31), 55000m),
            Contract("BANKNIFTY-NOV-55000-PE", InstrumentType.PutOption,
                new DateOnly(2026, 11, 26), 55000m)
        };

        var selected = HistoricalBankNiftyOptionSelector.Select(contracts, day,
            Direction.Buy, 55020m);

        Assert.Equal("BANKNIFTY-NOV-55000-CE", selected?.TradingSymbol);
    }

    [Fact]
    public void InsufficientMetadataCannotCreateMonetaryCandidate()
    {
        var day = new DateOnly(2026, 10, 29);
        var unverified = Contract("BANKNIFTY-NOV-55000-CE",
            InstrumentType.CallOption, new DateOnly(2026, 11, 26), 55000m) with
        { LotSize = 0 };

        Assert.Null(HistoricalBankNiftyOptionSelector.Select([unverified], day,
            Direction.Buy, 55000m));
    }

    private static NiftyOptionContractCandidate Contract(string symbol,
        InstrumentType type, DateOnly expiry, decimal strike) =>
        new(Guid.NewGuid(), symbol, type, expiry, strike, 30, .05m);
}
