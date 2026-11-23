using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Broker.Upstox;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.IntegrationTests;

public sealed class UpstoxExecutionPersistenceTests
{
    [DockerFact]
    public async Task PersistedOwnershipPartialFillsAndStopCancelRacesRemainSafeAfterRestart()
    {
        await using var container = new PostgreSqlBuilder("postgres:18.4-alpine").Build();
        await container.StartAsync(CancellationToken.None);
        var clock = new Clock();
        var services = new ServiceCollection().AddSingleton<TimeProvider>(clock)
            .AddDbContext<TradingDbContext>(o => o.UseNpgsql(container.GetConnectionString()))
            .AddScoped<IUpstoxExecutionCredentialProvider, Credentials>();
        await using var provider = services.BuildServiceProvider();
        var instrument = new Instrument(Guid.NewGuid(), "NSE", "NIFTY26NOV25000CE",
            InstrumentSegment.FuturesAndOptions, InstrumentType.CallOption, clock.GetUtcNow());
        instrument.UpdateBrokerMetadata("123", instrument.TradingSymbol, new(2026, 11, 30), 25000, 50, .05m);
        var source = Guid.NewGuid();
        var intent = new LiveExecutionIntent(Guid.NewGuid(), "NIFTY", source, "NIFTY", instrument.Id,
            Direction.Buy, 100, 100, 90, 120, "LE1234567890123456", clock.GetUtcNow(), "Upstox", "test-account");
        intent.BeginEntrySubmission();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.MigrateAsync();
            db.Instruments.Add(instrument); db.LiveExecutionIntents.Add(intent); await db.SaveChangesAsync();
        }
        using var handler = new BrokerHandler(intent);
        var factory = new Factory(handler);
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var config = Options.Create(new UpstoxExecutionOptions { Enabled = true, StaticIpVerified = true, AccountId = "test-account" });
        using var http = new UpstoxExecutionHttp(factory, scopes, config, clock);
        using var catalog = new UpstoxPaperDataClient(factory, new ConfigurationBuilder().Build(), scopes, new UpstoxPaperQuoteCache(), clock);
        var gateway = new UpstoxBrokerGateway(http, catalog, scopes, config, clock);
        var partial = await gateway.GetOrderAsync(intent.ClientReference, default);
        Assert.NotNull(partial); Assert.Equal(OrderState.PartiallyFilled, partial.State);
        Assert.Equal(50, partial.FilledQuantity); Assert.Equal(101m, partial.AverageFillPrice);
        // Duplicate trade_id is one fill, not another fifty units.
        Assert.Equal(50, partial.PricedQuantity);
        handler.CancelFillsRemaining = true;
        var raced = await gateway.CancelAsync(intent.ClientReference, default);
        Assert.Equal(OrderState.Filled, raced.State); Assert.Equal(100, raced.FilledQuantity);
        Assert.Equal(102m, raced.AverageFillPrice); Assert.Equal(1, handler.Writes);
        var stop = await gateway.ConfirmProtectionAsync("stop-1", 100, 90, default);
        Assert.Equal("TRIGGER_PENDING", stop.Status);
        handler.StopFillsOnCancel = true;
        await Assert.ThrowsAsync<BrokerOrderOutcomeUnknownException>(() => gateway.CancelProtectionAsync("stop-1", default));
        Assert.Equal(2, handler.Writes);
        // A recreated adapter reads persisted identity; it does not place another entry.
        var restarted = new UpstoxBrokerGateway(http, catalog, scopes, config, clock);
        Assert.Equal(OrderState.Filled, (await restarted.GetOrderAsync(intent.ClientReference, default))!.State);
        var wrongAccount = new UpstoxBrokerGateway(http, catalog, scopes,
            Options.Create(new UpstoxExecutionOptions { AccountId = "another-account" }), clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrongAccount.GetOrderAsync(intent.ClientReference, default));
        Assert.Equal(2, handler.Writes);
        await using var verification = provider.CreateAsyncScope();
        var saved = verification.ServiceProvider.GetRequiredService<TradingDbContext>();
        Assert.Equal("Upstox", (await saved.LiveExecutionIntents.SingleAsync()).ExecutionBroker);
        saved.LiveExecutionIntents.Add(new(Guid.NewGuid(), "NIFTY", source, "NIFTY", instrument.Id,
            Direction.Buy, 100, 100, 90, 120, "LE9999999999999999", clock.GetUtcNow()));
        await Assert.ThrowsAsync<DbUpdateException>(() => saved.SaveChangesAsync());
        Assert.Empty(await saved.PaperBrokerEvents.ToArrayAsync());
    }

    private sealed class Clock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 11, 23, 4, 0, 0, TimeSpan.Zero); }
    private sealed class Credentials : IUpstoxExecutionCredentialProvider
    {
        public Task<UpstoxExecutionAuthorization.Credential> ReadAsync(CancellationToken token) =>
            Task.FromResult(new UpstoxExecutionAuthorization.Credential("fixture-only", "test-account", ["NFO", "BFO"], false));
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new("https://fixture.invalid/") }; }
    private sealed class BrokerHandler(LiveExecutionIntent intent) : HttpMessageHandler
    {
        public bool CancelFillsRemaining { get; set; }
        public bool StopFillsOnCancel { get; set; }
        public int Writes { get; private set; }
        private bool complete;
        private bool stopFilled;
        private object Entry() => new { tag = intent.ClientReference, instrument_token = "NSE_FO|123", placed_by = "test-account",
            product = "D", transaction_type = "BUY", quantity = 100, filled_quantity = complete ? 100 : 50,
            status = complete ? "complete" : "open", order_id = "entry-1" };
        private object Stop() => new { tag = "LP" + intent.Id.ToString("N")[..16], instrument_token = "NSE_FO|123",
            placed_by = "test-account", product = "D", transaction_type = "SELL", quantity = 100,
            filled_quantity = stopFilled ? 100 : 0, status = stopFilled ? "complete" : "trigger pending",
            order_id = "stop-1", order_type = "SL", trigger_price = 90m, price = 88.2m };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object data;
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method == HttpMethod.Delete)
            {
                Writes++;
                if (path.Contains("entry-1", StringComparison.Ordinal)) complete = CancelFillsRemaining;
                else stopFilled = StopFillsOnCancel;
                data = new { order_id = path.Contains("entry-1", StringComparison.Ordinal) ? "entry-1" : "stop-1" };
            }
            else if (path.StartsWith("/v2/order/retrieve-all", StringComparison.Ordinal)) data = new[] { Entry() };
            else if (path.StartsWith("/v2/order/details", StringComparison.Ordinal)) data = Stop();
            else if (path.StartsWith("/v2/order/trades", StringComparison.Ordinal))
            {
                var first = new { trade_id = "fill-1", order_id = "entry-1", transaction_type = "BUY", quantity = 50, average_price = 101m };
                data = complete ? new[] { first, new { trade_id = "fill-2", order_id = "entry-1", transaction_type = "BUY", quantity = 50, average_price = 103m } }
                    : new[] { first, first };
            }
            else throw new InvalidOperationException("Unexpected fixture request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(new { status = "success", data })) });
        }
    }
}
