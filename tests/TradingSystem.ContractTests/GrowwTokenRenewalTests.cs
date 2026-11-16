using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Infrastructure.Broker.Groww;

namespace TradingSystem.ContractTests;

public sealed class GrowwTokenRenewalTests
{
    [Fact]
    public void ReservationSurvivesRestartAndBoundsRollingDayAttempts()
    {
        var now = DateTimeOffset.UnixEpoch;
        var state = new GrowwRenewalState(now, [], Guid.Empty, 0, "Ready");
        for (var i = 0; i < GrowwTokenRenewalPolicy.MaximumAttempts; i++)
        {
            var next = GrowwTokenRenewalPolicy.Reserve(state, now.AddMinutes(i * 2));
            Assert.NotEqual(state.Owner, next.Owner);
            Assert.Equal(next.Owner, GrowwTokenRenewalPolicy.Reserve(next, now.AddMinutes(i * 2 + 1)).Owner);
            state = next;
        }
        var blocked = GrowwTokenRenewalPolicy.Reserve(state, now.AddHours(5));
        Assert.Equal(state.Owner, blocked.Owner);
        Assert.Equal(now.AddHours(24), blocked.NextAttemptAtUtc);
        var resumed = GrowwTokenRenewalPolicy.Reserve(blocked, now.AddHours(24));
        Assert.NotEqual(blocked.Owner, resumed.Owner);
        Assert.Equal(96, resumed.Attempts.Length);
    }

    [Theory]
    [InlineData(429, 15)]
    [InlineData(401, 15)]
    [InlineData(403, 15)]
    [InlineData(500, 2)]
    public void FailuresBackOffAndRespectLongerBrokerDelay(int status, int minutes)
    {
        var now = DateTimeOffset.UnixEpoch;
        var state = new GrowwRenewalState(now, [], Guid.NewGuid(), 0, "");
        Assert.Equal(now.AddMinutes(minutes), GrowwTokenRenewalPolicy.Failed(state, now, status, null).NextAttemptAtUtc);
        Assert.Equal(now.AddHours(3), GrowwTokenRenewalPolicy.Failed(state, now, status, now.AddHours(3)).NextAttemptAtUtc);
    }

    [Fact]
    public async Task ManyQuoteCallersSendOnlyOneFailedRenewalAndStoredTokenBypassesCooldown()
    {
        using var fixture = new Fixture(HttpStatusCode.TooManyRequests);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Provider.GetAccessTokenAsync(default))));
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.Equal(1, fixture.Gate.Completions);
        Assert.Equal(fixture.Clock.Now.AddHours(1), fixture.Gate.RetryAfter);
        fixture.Vault.Value = "a-valid-shared-token-from-another-host";
        Assert.Equal(fixture.Vault.Value, await fixture.Provider.GetAccessTokenAsync(default));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task SuccessfulRenewalIsSavedOnceAndReusedByAllMarkets()
    {
        using var fixture = new Fixture(HttpStatusCode.OK);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ => await fixture.Provider.GetAccessTokenAsync(default)));
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.Equal(1, fixture.Vault.Saves);
        Assert.True(fixture.Gate.Success);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string keyName = "TEST_GROWW_KEY_" + Guid.NewGuid().ToString("N");
        private readonly string secretName = "TEST_GROWW_SECRET_" + Guid.NewGuid().ToString("N");
        private readonly ServiceProvider services;
        public readonly Clock Clock = new();
        public readonly Vault Vault = new();
        public readonly Gate Gate = new();
        public readonly Handler Handler;
        public readonly EnvironmentGrowwAccessTokenProvider Provider;
        public Fixture(HttpStatusCode status)
        {
            Environment.SetEnvironmentVariable(keyName, "synthetic-test-key");
            Environment.SetEnvironmentVariable(secretName, "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
            Handler = new(status);
            services = new ServiceCollection().AddSingleton<IGrowwTokenVault>(Vault)
                .AddSingleton<IGrowwTokenRenewalGate>(Gate).BuildServiceProvider();
            Provider = new(Options.Create(new GrowwOptions { TotpApiKeyEnvironmentVariable = keyName,
                TotpSecretEnvironmentVariable = secretName }), services.GetRequiredService<IServiceScopeFactory>(),
                new Factory(Handler), Clock);
        }
        public void Dispose()
        {
            Provider.Dispose(); services.Dispose(); Handler.Dispose();
            Environment.SetEnvironmentVariable(keyName, null); Environment.SetEnvironmentVariable(secretName, null);
        }
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://example.test") }; }
    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var response = new HttpResponseMessage(status) { Content = new StringContent("{\"token\":\"synthetic-valid-token-for-test\",\"isActive\":true}") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        }
    }
    private sealed class Gate : IGrowwTokenRenewalGate
    {
        public int Completions; public DateTimeOffset? RetryAfter; public bool Success;
        public Task<GrowwRenewalClaim> ClaimAsync(CancellationToken token) => Task.FromResult(new GrowwRenewalClaim(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(2), "Reserved"));
        public Task CompleteAsync(Guid owner, bool success, int? statusCode, DateTimeOffset? retryAfter, CancellationToken token)
        { Completions++; Success = success; RetryAfter = retryAfter; return Task.CompletedTask; }
    }
    private sealed class Vault : IGrowwTokenVault
    {
        public string? Value; public int Saves;
        public Task<string?> GetValidTokenAsync(CancellationToken token) => Task.FromResult(Value);
        public Task<GrowwTokenStatus> GetStatusAsync(CancellationToken token) => Task.FromResult(new GrowwTokenStatus(Value is not null, false, null, null, "test"));
        public Task<GrowwTokenStatus> StoreAsync(string accessToken, string actor, CancellationToken token)
        { Value = accessToken; Saves++; return GetStatusAsync(token); }
    }
}
