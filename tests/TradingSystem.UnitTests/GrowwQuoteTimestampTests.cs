using TradingSystem.Application.Broker;
using TradingSystem.Application.MarketData;

namespace TradingSystem.UnitTests;

public sealed class GrowwQuoteTimestampTests
{
    [Theory]
    [InlineData(1793866950L)]
    [InlineData(1793866950000L)]
    public void RecordedSecondsAndMillisecondsPreserveTheSameSourceInstant(long epoch)
    {
        var source = DateTimeOffset.FromUnixTimeSeconds(1793866950L);
        var quote = new GrowwQuote(756.55m, epoch, null, null, null, null, 18927m, 3067m, 609.8m);
        var received = source.AddSeconds(4);
        Assert.Equal(source, GrowwQuoteTimestamp.ToUtc(epoch));
        var replay = HistoricalQuoteProjection.ToReplay(new(Guid.NewGuid(), received,
            new("BSE", "FNO", "SENSEX26N1279300CE"), quote), "SENSEX26N1279300CE");
        Assert.Equal(source, replay.SourceAtUtc);
        var normalized = new GrowwQuoteNormalizer().Normalize(Guid.NewGuid(), quote, received);
        Assert.Equal(source, normalized.SourceTimestampUtc);
    }

    [Fact]
    public void InvalidAndMissingEpochAreNotInvented()
    {
        Assert.Null(GrowwQuoteTimestamp.ToUtc(null));
        Assert.Null(GrowwQuoteTimestamp.ToUtc(0));
        Assert.Null(GrowwQuoteTimestamp.ToUtc(-1));
        Assert.Null(GrowwQuoteTimestamp.ToUtc(long.MaxValue));
    }
}
