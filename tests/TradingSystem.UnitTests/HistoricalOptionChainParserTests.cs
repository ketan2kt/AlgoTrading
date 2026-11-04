using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalOptionChainParserTests
{
    [Fact]
    public void ParsesOnlyValidStrikeObjectsWithoutInventingOi()
    {
        var strikes = HistoricalOptionChainParser.Parse("""
            {"selectedStrikes":[
              {"strikePrice":22000,"call":{"openInterest":500},
               "put":{"openInterest":300}},
              {"strikePrice":22050,"call":{},"put":{"openInterest":100}},
              {"strikePrice":0,"call":{"openInterest":999}}
            ]}
            """);

        Assert.Equal(2, strikes.Count);
        Assert.Equal(500m, strikes[0].CallOpenInterest);
        Assert.Equal(300m, strikes[0].PutOpenInterest);
        Assert.Equal(0m, strikes[1].CallOpenInterest);
        Assert.Equal(100m, strikes[1].PutOpenInterest);
        Assert.Empty(HistoricalOptionChainParser.Parse("not-json"));
    }

    [Fact]
    public void ChainReceiptProducesTimestampedNonExecutableLtpObservations()
    {
        var received = new DateTimeOffset(2026, 10, 29, 4, 10, 8,
            TimeSpan.Zero);
        var quotes = HistoricalOptionChainParser.ParseQuoteObservations("""
            {"selectedStrikes":[
              {"strikePrice":22000,
               "call":{"tradingSymbol":"NIFTY22000CE","lastPrice":105.5,
                       "openInterest":750,"volume":42},
               "put":{"tradingSymbol":"NIFTY22000PE","lastPrice":90}},
              {"strikePrice":22050,"call":null}
            ]}
            """, received);

        Assert.Equal(2, quotes.Count);
        var quote = quotes[0];
        Assert.Equal("NIFTY22000CE", quote.Symbol);
        Assert.Equal(received, quote.AvailableAtUtc);
        Assert.Equal(105.5m, quote.LastPrice);
        Assert.Equal(750m, quote.OpenInterest);
        Assert.Equal(42, quote.Volume);
        Assert.Equal("GrowwChainReceiptLtp", quote.Source);
        Assert.Null(quote.BidPrice);
        Assert.Null(quote.OfferPrice);
        Assert.Null(quotes[1].OpenInterest);
        Assert.Empty(HistoricalOptionChainParser.ParseQuoteObservations("bad", received));
    }
}
