using System.Net;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Broker.Upstox;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.ContractTests;

public sealed class UpstoxExecutionContractTests
{
    private static readonly string[] SourceIdentity = ["SourceType", "SourceId"];
    [Fact]
    public void LegacyTradesStayGrowwAndNewOwnershipIsExplicit()
    {
        var legacy = Intent();
        Assert.Equal("Groww", legacy.ExecutionBroker);
        Assert.Equal("primary", legacy.ExecutionAccount);
        var selected = Intent("Upstox", "test-account");
        selected.BeginEntrySubmission();
        Assert.Equal("Upstox", selected.ExecutionBroker);
        Assert.Equal("test-account", selected.ExecutionAccount);
        Assert.Throws<ArgumentException>(() => Intent("Other", "test-account"));
    }

    [Fact]
    public void OwnershipMigrationIsAdditiveAndPreservesGlobalSourceDeduplication()
    {
        using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_model").Options, TimeProvider.System);
        var sql = db.GetService<IMigrator>().GenerateScript("20261123123000_PersistExternalLiveExitReconciliation",
            "20261123134033_PersistLiveExecutionBrokerOwnership");
        Assert.Contains("DEFAULT 'Groww'", sql);
        Assert.Contains("DEFAULT 'primary'", sql);
        Assert.DoesNotContain("DROP ", sql);
        Assert.False(db.Database.HasPendingModelChanges());
        var model = db.Model.FindEntityType(typeof(LiveExecutionIntent))!;
        Assert.Contains(model.GetIndexes(), x => x.IsUnique &&
            x.Properties.Select(p => p.Name).SequenceEqual(SourceIdentity));
    }

    [Theory]
    [InlineData("complete", 50, 50, OrderState.Filled)]
    [InlineData("complete", 25, 50, OrderState.ReconciliationRequired)]
    [InlineData("cancelled", 25, 50, OrderState.Cancelled)]
    [InlineData("open", 25, 50, OrderState.PartiallyFilled)]
    [InlineData("trigger pending", 0, 50, OrderState.BrokerAcknowledged)]
    [InlineData("rejected", 25, 50, OrderState.ReconciliationRequired)]
    public void PartialCancelledAndUnknownStatusesNeverPretendZeroExposure(string status, int filled, int total, OrderState expected)
        => Assert.Equal(expected, UpstoxBrokerGateway.MapState(status, filled, total));

    [Fact]
    public void AuthorizationExpiresAtNext0330IstAndBrokerTimestampsAreIst()
    {
        Assert.Equal(At("2026-11-23T22:00:00Z"),
            UpstoxExecutionAuthorization.NextExpiry(At("2026-11-23T04:00:00Z")));
        Assert.Equal(At("2026-11-23T22:00:00Z"),
            UpstoxExecutionAuthorization.NextExpiry(At("2026-11-23T21:59:59Z")));
        Assert.Equal(At("2026-11-24T22:00:00Z"),
            UpstoxExecutionAuthorization.NextExpiry(At("2026-11-23T22:00:00Z")));
        Assert.Equal(At("2026-11-23T04:00:00Z"), UpstoxBrokerGateway.IndiaTimestamp("2026-11-23 09:30:00"));
    }

    [Fact]
    public async Task SandboxFlagCannotRouteToProductionOrBypassStaticIpVerification()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must never send"));
        using var services = Services();
        using var http = new UpstoxExecutionHttp(new Factory(handler), services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new UpstoxExecutionOptions { Enabled = true, Sandbox = true }), new Clock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.SendAsync(HttpMethod.Post, "v3/order/place", new { quantity = 50 }, default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task HttpTimeoutIsNotRetried()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("synthetic disconnect"));
        using var services = Services();
        using var http = Client(handler, services);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(HttpMethod.Post, "v3/order/place", new { quantity = 50 }, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RateLimitHonorsBackoffWithoutRepeatedRequestsOrResponseLeak()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
            Content = new StringContent("DO-NOT-LOG-BROKER-RESPONSE") }));
        using var services = Services();
        using var http = Client(handler, services);
        var error = await Assert.ThrowsAsync<UpstoxExecutionException>(() => http.SendAsync(HttpMethod.Put, "v3/order/modify", new { quantity = 50 }, default));
        Assert.DoesNotContain("DO-NOT-LOG", error.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.SendAsync(HttpMethod.Get, "v2/order/retrieve-all", null, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MutationsUseExecutionCredentialAndCombinedBudget()
    {
        using var handler = new Handler((request, _) => {
            Assert.Equal("test-execution-only", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"success\",\"data\":{}}") });
        });
        using var services = Services();
        using var http = Client(handler, services);
        for (var i=0; i<8; i++) await http.SendAsync(HttpMethod.Put, "v3/order/modify", new { quantity=50 }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.SendAsync(HttpMethod.Post, "v3/order/place", new {quantity=50}, default));
        Assert.Equal(8, handler.Calls);
    }

    private static LiveExecutionIntent Intent(string broker="Groww", string account="primary") =>
        new(Guid.NewGuid(), "NIFTY", Guid.NewGuid(), "NIFTY", Guid.NewGuid(), Direction.Buy, 50, 100, 90, 120,
            "LE1234567890", DateTimeOffset.UtcNow, broker, account);
    private static ServiceProvider Services() => new ServiceCollection()
        .AddScoped<IUpstoxExecutionCredentialProvider, Credentials>().BuildServiceProvider();
    private static UpstoxExecutionHttp Client(Handler handler, ServiceProvider services) =>
        new(new Factory(handler), services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new UpstoxExecutionOptions { Enabled=true, StaticIpVerified=true }), new Clock());
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => At("2026-11-23T04:00:00Z"); }
    private sealed class Credentials : IUpstoxExecutionCredentialProvider {
        public Task<UpstoxExecutionAuthorization.Credential> ReadAsync(CancellationToken token) =>
            Task.FromResult(new UpstoxExecutionAuthorization.Credential("test-execution-only", "test-account", ["NFO", "BFO"], false));
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://execution.invalid/") };
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
        public int Calls {get; private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request, token); }
    }
}
