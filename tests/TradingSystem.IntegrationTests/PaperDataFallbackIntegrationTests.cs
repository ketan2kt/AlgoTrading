using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using TradingSystem.Application.Broker;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Broker;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.IntegrationTests;

public sealed class PaperDataFallbackIntegrationTests
{
    [DockerFact]
    public async Task PrimaryOutageAllThreeUseVerifiedBackupDataAndRecoverSessionStateAfterRestart()
    {
        var local = Environment.GetEnvironmentVariable("PAPER_BACKUP_TEST_CONNECTION");
        // Optional loopback-only scratch database for Windows hosts without Docker.
        if (local is not null && !local.StartsWith("Host=127.0.0.1;", StringComparison.Ordinal))
            throw new InvalidOperationException("Only an explicit loopback test database is permitted.");
        await using var container = local is null ? new PostgreSqlBuilder("postgres:18.4-alpine").Build() : null;
        if (container is not null) await container.StartAsync(CancellationToken.None);
        var connection = local ?? container!.GetConnectionString();
        var clock = new Clock();
        var services = new ServiceCollection().AddSingleton<TimeProvider>(clock);
        services.AddDbContext<TradingDbContext>(o => o.UseNpgsql(connection));
        services.AddSingleton(new MarketDataValidator(clock, TimeSpan.FromSeconds(30)));
        services.AddSingleton(new CandleAggregator(60));
        services.AddSingleton<MarketDataHealthMonitor>();
        services.AddScoped<IMarketDataPersistence, EfMarketDataPersistence>();
        services.AddScoped<MarketDataProcessor>();
        services.AddSingleton<PaperBrokerStateStore>();
        services.AddSingleton<IPaperBrokerJournal, EfPaperBrokerJournal>();
        services.AddSingleton<PaperBrokerGateway>();
        await using var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var instruments = new List<Instrument>();
        var catalog = new List<object>();
        foreach (var (market, exchange, lot) in new[] { ("NIFTY", "NSE", 65), ("BANKNIFTY", "NSE", 30), ("SENSEX", "BSE", 20) })
        {
            var index = new Instrument(Guid.NewGuid(), exchange, market, InstrumentSegment.Cash, InstrumentType.Index, clock.Now);
            index.UpdateIndexBrokerMetadata((instruments.Count + 1).ToString(CultureInfo.InvariantCulture), market + "-INDEX");
            instruments.Add(index);
            catalog.Add(new { instrument_type = "INDEX", segment = exchange + "_INDEX", instrument_key = UpstoxReplayBackupCapture.IndexKey(market) });
            foreach (var type in new[] { InstrumentType.Future, InstrumentType.CallOption, InstrumentType.PutOption })
            {
                var side = type == InstrumentType.Future ? "FUT" : type == InstrumentType.CallOption ? "CE" : "PE";
                var instrument = new Instrument(Guid.NewGuid(), exchange, market + "26NOV100" + side, InstrumentSegment.FuturesAndOptions, type, clock.Now);
                var number = (instruments.Count + 100).ToString(CultureInfo.InvariantCulture);
                instrument.UpdateBrokerMetadata(number, market + "-" + side, new(2026, 11, 24), type == InstrumentType.Future ? null : 100, lot, .05m);
                instruments.Add(instrument);
                catalog.Add(new { instrument_type = side, segment = exchange + "_FO", instrument_key = exchange + "_FO|" + number,
                    underlying_symbol = market, lot_size = lot, tick_size = 5, strike_price = 100,
                    expiry = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(5.5)).ToUnixTimeMilliseconds() });
            }
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Instruments.AddRange(instruments); await db.SaveChangesAsync();
        }
        using var handler = new DataHandler(clock, catalog, instruments);
        var factory = new Factory(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ReplayBackup:UpstoxAnalyticsToken"] = "test-read-only-token" }).Build();
        var cache = new UpstoxPaperQuoteCache();
        using var client = new UpstoxPaperDataClient(factory, config, scopes, cache, clock);
        using var state = new PaperDataSource(scopes, clock);
        var paperBroker = provider.GetRequiredService<PaperBrokerGateway>();
        var niftyCall = instruments.Single(x => x.Type == InstrumentType.CallOption && x.TradingSymbol.StartsWith("NIFTY", StringComparison.Ordinal));
        var orderRequest = new BrokerOrderRequest("continuity-entry", niftyCall.Id, Direction.Buy, 65, 100, 20);
        await paperBroker.SubmitAsync(orderRequest, CancellationToken.None);
        var partialEntry = await paperBroker.ProcessNextFillAsync(orderRequest.ClientReference, CancellationToken.None);
        Assert.Equal(OrderState.PartiallyFilled, partialEntry.State);
        Assert.Equal(20, partialEntry.FilledQuantity);
        var primary = new FailedPrimary(clock, instruments);
        var gateway = new PaperMarketDataGateway(primary, client, state, clock);
        await client.PrepareAsync(CancellationToken.None);
        foreach (var instrument in instruments)
        {
            var key = instrument.Type == InstrumentType.Index ? UpstoxReplayBackupCapture.IndexKey(instrument.TradingSymbol)! :
                instrument.Exchange + "_FO|" + instrument.ExchangeToken;
            // Existing recorder's warm snapshots are reused without another request.
            cache.Observe(key, instrument.Type == InstrumentType.Index
                ? new(100, null, null, null, null, null, null, null, null)
                : new(100, clock.Now.ToUnixTimeMilliseconds(), 99, 500, 101, 600, 1000, 100, null), clock.Now);
            var quote = await gateway.GetQuoteAsync(new(instrument.Exchange, instrument.Type == InstrumentType.Index ? "CASH" : "FNO",
                instrument.TradingSymbol), CancellationToken.None);
            Assert.Equal("Upstox", quote.Source);
            if (instrument.Type != InstrumentType.Index) { Assert.Equal(500, quote.BidQuantity); Assert.Equal(600, quote.OfferQuantity); }
            var history = await gateway.GetHistoricalCandlesAsync(new(instrument.Exchange, "FNO", instrument.GrowwSymbol!,
                clock.Now.AddMinutes(-2), clock.Now, "1minute"), CancellationToken.None);
            Assert.Equal("Upstox", history.Source); Assert.Single(history.Candles);
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var importer = new GrowwHistoricalCandleImporter(db);
            Assert.Equal(1, await importer.ImportAsync(instrument, history, clock.Now, CancellationToken.None));
            Assert.Equal(0, await importer.ImportAsync(instrument, history, clock.Now, CancellationToken.None));
            var candle = await db.Candles.SingleAsync(x => x.InstrumentId == instrument.Id);
            Assert.Equal("Upstox", candle.Source); Assert.Equal(200, candle.Volume);
        }
        Assert.Equal(3, primary.QuoteCalls); Assert.Equal("Upstox", gateway.Source);
        Assert.Equal(0, handler.QuoteCalls);
        foreach (var market in new[] { "NIFTY", "BANKNIFTY", "SENSEX" })
        {
            var chain = await gateway.GetOptionChainAsync(new(market == "SENSEX" ? "BSE" : "NSE", market, new(2026, 11, 24)), CancellationToken.None);
            Assert.StartsWith(market, Assert.Single(chain.Strikes).Call!.TradingSymbol, StringComparison.Ordinal);
        }
        using var restarted = new PaperDataSource(scopes, clock);
        await restarted.InitializeAsync(CancellationToken.None);
        Assert.Equal("Upstox", restarted.Source);
        primary.Available = true;
        using var continuity = new PaperDataContinuityService(client, primary, restarted, scopes,
            new GrowwQuoteNormalizer(), clock, NullLogger<PaperDataContinuityService>.Instance);
        await continuity.WarmAsync("SENSEX", false, CancellationToken.None);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal(2, await db.MarketObservations.CountAsync(x => x.Source == "Groww"));
            Assert.Equal(2, await db.Candles.CountAsync(x => x.Source == "Groww"));
            Assert.Equal(12, await db.Candles.CountAsync(x => x.Source == "Upstox"));
        }
        await restarted.UsePrimaryAsync("SENSEX", CancellationToken.None);
        await continuity.WarmAsync("NIFTY", false, CancellationToken.None);
        Assert.Contains(niftyCall.TradingSymbol, primary.QuotedSymbols);
        Assert.Equal("Groww", restarted.GetSource("sensex"));
        Assert.Equal("Upstox", restarted.GetSource("bank-nifty"));
        Assert.Equal("Upstox", restarted.GetSource("nifty"));
        using var secondRestart = new PaperDataSource(scopes, clock);
        await secondRestart.InitializeAsync(CancellationToken.None);
        Assert.Equal("Groww", secondRestart.GetSource("SENSEX"));
        Assert.Equal("Upstox", secondRestart.GetSource("BANKNIFTY"));
        var sensexId = instruments.Single(x => x.TradingSymbol == "SENSEX").Id;
        Assert.Equal("Groww", secondRestart.GetSource(sensexId));
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var durable = await db.ApplicationSettings.SingleAsync(x => x.Key == PaperDataSource.SettingKey);
            Assert.Equal(TradingMode.Paper, durable.Mode);
            Assert.Equal("\"2026-11-16\"", durable.ValueJson); // Failback does not arm deferred backup signals for live execution.
            Assert.Empty(await db.LiveExecutionIntents.ToArrayAsync());
            Assert.Empty(await db.BrokerAccessTokenSecrets.ToArrayAsync());
        }
        var freshGateway = new PaperMarketDataGateway(primary, client, restarted, clock);
        // Exercise the actual uncached HTTP quote path, then bidirectional failover.
        var bankCall = instruments.Single(x => x.Type == InstrumentType.CallOption && x.TradingSymbol.StartsWith("BANKNIFTY", StringComparison.Ordinal));
        var bankRequest = new GrowwQuoteRequest("NSE", "FNO", bankCall.TradingSymbol);
        clock.Now = clock.Now.AddSeconds(6);
        var fetched = await freshGateway.GetQuoteAsync(bankRequest, CancellationToken.None);
        Assert.Equal("Upstox", fetched.Source);
        Assert.Equal(500, fetched.BidQuantity);
        Assert.Equal(1, handler.QuoteCalls);
        clock.Now = clock.Now.AddSeconds(6);
        restarted.RecordReady("BANKNIFTY", "Groww");
        handler.QuoteFailure = true;
        Assert.Equal("Groww", (await freshGateway.GetQuoteAsync(bankRequest, CancellationToken.None)).Source);
        Assert.Equal("Groww", restarted.GetSource("BANKNIFTY"));
        Assert.Equal("Upstox", restarted.GetSource("NIFTY")); // Independent routing.
        handler.QuoteFailure = false;
        primary.MissingDepth = true;
        clock.Now = clock.Now.AddSeconds(6);
        Assert.Equal("Upstox", (await freshGateway.GetQuoteAsync(bankRequest, CancellationToken.None)).Source);
        Assert.Equal("Upstox", restarted.GetSource("BANKNIFTY"));
        primary.MissingDepth = false;
        primary.Available = false;
        handler.QuoteFailure = true;
        clock.Now = clock.Now.AddSeconds(6);
        await Assert.ThrowsAsync<GrowwApiException>(() => freshGateway.GetQuoteAsync(bankRequest, CancellationToken.None));
        Assert.Equal("Upstox", restarted.GetSource("BANKNIFTY")); // No stale quote or fabricated healthy route.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => freshGateway.GetQuoteAsync(bankRequest, cancelled.Token));
        // Routing and failures must not fill, resubmit, close or reset an existing order.
        Assert.Equal(partialEntry, await paperBroker.SubmitAsync(orderRequest, CancellationToken.None));
        var restoredBroker = new PaperBrokerGateway(new PaperBrokerStateStore(), provider.GetRequiredService<IPaperBrokerJournal>(), clock);
        Assert.Equal(partialEntry, await restoredBroker.GetOrderAsync(orderRequest.ClientReference, CancellationToken.None));
        var position = Assert.Single(await restoredBroker.GetPositionsAsync(CancellationToken.None));
        Assert.Equal(20, position.Quantity);
        Assert.Equal(100m, position.AverageEntryPrice);
        primary.Available = false;
        await Assert.ThrowsAsync<GrowwApiException>(() => freshGateway.GetUserProfileAsync(CancellationToken.None));
        clock.Now = clock.Now.AddDays(1);
        await restarted.InitializeAsync(CancellationToken.None);
        Assert.Equal("Groww", restarted.Source);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 16, 5, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class DataHandler(Clock clock, List<object> catalog, List<Instrument> instruments) : HttpMessageHandler
    {
        public int QuoteCalls;
        public bool QuoteFailure;
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            var uri = request.RequestUri!;
            if (uri.Host == "assets.upstox.com")
            {
                using var stream = new MemoryStream();
                using (var gzip = new GZipStream(stream, CompressionMode.Compress, true))
                    gzip.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(catalog)));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) });
            }
            Assert.Equal("api.upstox.com", uri.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            object payload;
            if (uri.AbsolutePath.StartsWith("/v3/historical-candle/", StringComparison.Ordinal))
                payload = new { status = "success", data = new { candles = new object[][] {
                    [clock.Now.AddMinutes(-1).ToOffset(TimeSpan.FromHours(5.5)).ToString("O", CultureInfo.InvariantCulture), 100, 110, 90, 105, 200, 100] } } };
            else if (uri.AbsolutePath == "/v2/option/chain")
            {
                var query = Uri.UnescapeDataString(uri.Query);
                var market = query.Contains("SENSEX", StringComparison.Ordinal) ? "SENSEX" : query.Contains("Nifty Bank", StringComparison.Ordinal) ? "BANKNIFTY" : "NIFTY";
                var option = instruments.Single(x => x.Type == InstrumentType.CallOption && x.TradingSymbol.StartsWith(market, StringComparison.Ordinal));
                payload = new { status = "success", data = new[] { new { underlying_key = UpstoxReplayBackupCapture.IndexKey(market),
                    expiry = "2026-11-24", strike_price = 100, underlying_spot_price = 100,
                    call_options = new { instrument_key = option.Exchange + "_FO|" + option.ExchangeToken,
                        market_data = new { ltp = 100, oi = 1000, volume = 2000 }, option_greeks = new { delta = .5, gamma = .01, theta = -.1, vega = .2, iv = 20 } } } } };
            }
            else if (uri.AbsolutePath == "/v3/market-quote/quotes")
            {
                QuoteCalls++;
                if (QuoteFailure) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                payload = new { status = "success", data = instruments.Where(x => x.Type != InstrumentType.Index)
                    .ToDictionary(x => x.Exchange + "_FO|" + x.ExchangeToken, x => new {
                        instrument_token = x.Exchange + "_FO|" + x.ExchangeToken,
                        timestamp = clock.Now.ToString("O", CultureInfo.InvariantCulture),
                        last_trade_time = clock.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                        last_price = 100, volume = 1000, oi = 200,
                        depth = new { buy = new[] { new { price = 99, quantity = 500 } }, sell = new[] { new { price = 101, quantity = 600 } } }
                    }) };
            }
            else throw new InvalidOperationException("Unexpected endpoint.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) });
        }
    }
    private sealed class FailedPrimary(Clock clock, List<Instrument> instruments) : IGrowwReadOnlyGateway
    {
        public int QuoteCalls;
        public bool Available;
        public bool MissingDepth;
        public List<string> QuotedSymbols { get; } = [];
        private static GrowwApiException Outage() => new("Primary authentication unavailable", "AUTH", 429);
        public Task<GrowwQuote> GetQuoteAsync(GrowwQuoteRequest request, CancellationToken token)
        {
            QuoteCalls++;
            QuotedSymbols.Add(request.TradingSymbol);
            if (!Available) throw Outage();
            return Task.FromResult(new GrowwQuote(100, clock.Now.ToUnixTimeMilliseconds(), 99, MissingDepth ? 0 : 500, 101, 600, 1000, 200, null));
        }
        public Task<GrowwHistoricalCandles> GetHistoricalCandlesAsync(GrowwHistoricalCandleRequest request, CancellationToken token)
        {
            if (!Available) throw Outage();
            return Task.FromResult(new GrowwHistoricalCandles([new(clock.Now.AddMinutes(-1).ToOffset(TimeSpan.FromHours(5.5))
                .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), 100, 110, 90, 105, 200, 100)], 105, 1));
        }
        public Task<GrowwOptionChain> GetOptionChainAsync(GrowwOptionChainRequest request, CancellationToken token)
        {
            if (!Available) throw Outage();
            GrowwOptionContract Contract(InstrumentType type) => new(instruments.Single(x => x.Type == type &&
                x.TradingSymbol.StartsWith(request.Underlying, StringComparison.Ordinal)).TradingSymbol, 100, 1000, 2000, null);
            return Task.FromResult(new GrowwOptionChain(100, [new(100, Contract(InstrumentType.CallOption), Contract(InstrumentType.PutOption))]));
        }
        public Task<GrowwUserProfile> GetUserProfileAsync(CancellationToken token) => throw Outage();
        public Task<IReadOnlyList<GrowwPosition>> GetPositionsAsync(string segment, CancellationToken token) => throw Outage();
        public Task<IReadOnlyList<GrowwInstrumentRecord>> GetInstrumentMasterAsync(CancellationToken token) => throw Outage();
        public Task<IReadOnlyList<DateOnly>> GetHistoricalExpiriesAsync(string exchange, string underlyingSymbol, int year, int? month, CancellationToken token) => throw Outage();
        public Task<IReadOnlyList<string>> GetHistoricalContractsAsync(string exchange, string underlyingSymbol, DateOnly expiryDate, CancellationToken token) => throw Outage();
    }
}
