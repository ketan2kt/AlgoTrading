using System.Text.Json;
using TradingSystem.Application.Broker;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class PaperDataFallbackTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 16, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("BANKNIFTY")]
    [InlineData("SENSEX")]
    public void SuccessfulButStaleHistoryTriggersFallbackForEveryIndex(string symbol)
    {
        var request = new GrowwHistoricalCandleRequest("NSE", "FNO", symbol + "26NOVFUT", At.AddHours(-1), At, "1minute");
        var stale = new GrowwHistoricalCandles([new("2026-11-16T10:25:00", 100, 101, 99, 100, 20, null)], 100, 1);
        Assert.Throws<GrowwApiException>(() => PaperMarketDataGateway.ValidateCurrentHistory(request, stale, At));
        Assert.Throws<GrowwApiException>(() => PaperMarketDataGateway.ValidateCurrentHistory(request, stale with { Candles = [] }, At));
        var current = stale with { Candles = [new("2026-11-16T10:29:00", 100, 101, 99, 100, 0, null)] };
        Assert.Same(current, PaperMarketDataGateway.ValidateCurrentHistory(request, current, At));
        Assert.Same(stale, PaperMarketDataGateway.ValidateCurrentHistory(request with { EndUtc = At.AddDays(-1), StartUtc = At.AddDays(-2) }, stale, At));
    }

    [Fact]
    public void UnclosedCandlesCannotProveCurrentHistoryReady()
    {
        var request = new GrowwHistoricalCandleRequest("BSE", "FNO", "SENSEX26NOVFUT", At.AddHours(-1), At, "1minute");
        var unclosed = new GrowwHistoricalCandles([new("2026-11-16T10:30:00", 100, 101, 99, 100, 20, null)], 100, 1);
        Assert.Throws<GrowwApiException>(() => PaperMarketDataGateway.ValidateCurrentHistory(request, unclosed, At));
        Assert.Same(unclosed, PaperMarketDataGateway.ValidateCurrentHistory(request with { EndUtc = At.AddHours(6) }, unclosed, At.AddHours(6)));
    }

    [Fact]
    public void FailbackRequiresSustainedRecoveryUnlessBackupIsUnavailable()
    {
        var recovery = new PaperProviderRecovery();
        Assert.False(recovery.Observe(true, At, true));
        Assert.False(recovery.Observe(true, At.AddSeconds(14), true));
        Assert.True(recovery.Observe(true, At.AddSeconds(15), true));
        Assert.False(recovery.Observe(false, At.AddSeconds(16), true));
        Assert.False(recovery.Observe(true, At.AddSeconds(17), true));
        Assert.True(recovery.Observe(true, At.AddSeconds(18), false));
        Assert.False(recovery.Observe(false, At.AddSeconds(19), false));
    }

    [Theory]
    [InlineData("NIFTY", "NSE", 65)]
    [InlineData("BANKNIFTY", "NSE", 30)]
    [InlineData("SENSEX", "BSE", 20)]
    public void ExactOptionMappingRejectsWrongSideStrikeLotExpiryAndTick(string market, string exchange, int lot)
    {
        var option = new Instrument(Guid.NewGuid(), exchange, market + "26NOV100CE", InstrumentSegment.FuturesAndOptions,
            InstrumentType.CallOption, At);
        option.UpdateBrokerMetadata("123", "native-symbol", new(2026, 11, 24), 100, lot, .05m);
        var expiry = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(5.5)).ToUnixTimeMilliseconds();
        bool Match(string side = "CE", decimal strike = 100, int? units = null, long? date = null, decimal tick = 5)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { underlying_symbol = market, instrument_type = side,
                instrument_key = exchange + "_FO|123", segment = exchange + "_FO", lot_size = units ?? lot,
                expiry = date ?? expiry, strike_price = strike, tick_size = tick }));
            return UpstoxPaperDataClient.Matches(json.RootElement, option);
        }
        Assert.True(Match()); Assert.False(Match(side: "PE")); Assert.False(Match(strike: 200));
        Assert.False(Match(units: lot + 1)); Assert.False(Match(date: expiry + 86_400_000)); Assert.False(Match(tick: 10));
    }

    [Fact]
    public void ProviderSwitchDoesNotCreateVolumeFromAnotherProvidersCounter()
    {
        var normalizer = new GrowwQuoteNormalizer(); var id = Guid.NewGuid();
        GrowwQuote Quote(decimal volume, string source) => new(100, At.ToUnixTimeMilliseconds(), 99, 10, 101, 10, volume, 1, null)
            { Source = source, ObservedAtUtc = At };
        Assert.Equal(0, normalizer.Normalize(id, Quote(100, "Groww"), At).VolumeDelta);
        var backup = normalizer.Normalize(id, Quote(10_000, "Upstox"), At);
        Assert.Equal("Upstox", backup.Source); Assert.Equal(0, backup.VolumeDelta);
        Assert.Equal(5, normalizer.Normalize(id, Quote(10_005, "Upstox"), At).VolumeDelta);
        Assert.Equal(2, normalizer.Normalize(id, Quote(102, "Groww"), At).VolumeDelta);
    }

    [Fact]
    public void IndexRetainsProviderObservationClockAndCacheRejectsFrozenOrFutureData()
    {
        var quote = new GrowwQuote(100, null, null, null, null, null, null, null, null);
        var cache = new UpstoxPaperQuoteCache();
        cache.Observe("NSE_INDEX|Nifty 50", quote, At);
        Assert.True(cache.TryGet("NSE_INDEX|Nifty 50", At.AddSeconds(3), out var actual));
        Assert.Null(actual.LastTradeTimeEpochMilliseconds);
        Assert.Equal(At, new GrowwQuoteNormalizer().Normalize(Guid.NewGuid(), actual, At.AddSeconds(3)).SourceTimestampUtc);
        Assert.False(cache.TryGet("NSE_INDEX|Nifty 50", At.AddSeconds(-1), out _));
        Assert.False(cache.TryGet("NSE_INDEX|Nifty 50", At.AddSeconds(5), out _));
        cache.Observe("option", quote with { LastTradeTimeEpochMilliseconds = At.AddSeconds(-31).ToUnixTimeMilliseconds() }, At);
        Assert.False(cache.TryGet("option", At, out _));
    }
}
