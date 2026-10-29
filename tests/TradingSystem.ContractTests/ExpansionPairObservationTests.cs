using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class ExpansionPairObservationTests
{
    [Fact]
    public void BasketLiquidationEstimateUsesBothBidsAndCostsAtOneObservation()
    {
        var now = new DateTimeOffset(2026, 10, 29, 7, 15, 0, TimeSpan.Zero);
        var call = new MarketPaperPosition(Guid.NewGuid(), "nifty", Guid.NewGuid(), Guid.NewGuid(),
            "Expansion Pair|pair|CE", Direction.Buy, 100, 60m, 1m, 600m, now);
        var put = new MarketPaperPosition(Guid.NewGuid(), "nifty", Guid.NewGuid(), Guid.NewGuid(),
            "Expansion Pair|pair|PE", Direction.Buy, 100, 60m, 1m, 600m, now);
        var quotes = new Dictionary<Guid, GrowwQuote>
        {
            [call.Id] = Quote(80m, 77m),
            [put.Id] = Quote(43m, 40m)
        };

        var estimate = IntradayExpansionPairService.EstimateNetLiquidationPnl([call, put], quotes);

        Assert.NotNull(estimate);
        Assert.True(estimate < -300m); // Combined bid value is below the ₹12,000 debit before costs.
        Assert.Null(IntradayExpansionPairService.EstimateNetLiquidationPnl([call, put],
            new Dictionary<Guid, GrowwQuote> { [call.Id] = quotes[call.Id] }));
    }

    private static GrowwQuote Quote(decimal last, decimal bid) =>
        new(last, null, bid, null, last + 1m, null, null, null, null);
}
