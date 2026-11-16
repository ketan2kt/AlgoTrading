using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingSystem.Infrastructure;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class UpstoxHttpClientTests
{
    [Theory]
    [InlineData("Website")]
    [InlineData("ReplayRecorder")]
    public async Task ProductionClientIdentifiesItselfWithoutLeakingQuoteCredentialsToCatalog(string role)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TradingDatabase"] = "Host=localhost;Database=unused",
            ["ApplicationRole"] = role,
            ["ReplayBackup:Host"] = "Website",
            ["ReplayBackup:UpstoxAnalyticsToken"] = "test-only-never-send-to-catalog"
        }).Build();
        var services = new ServiceCollection();
        services.AddTradingSystemInfrastructure(configuration);
        services.AddHttpClient(UpstoxReplayBackupCapture.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new CatalogHandler());
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(UpstoxReplayBackupCapture.ClientName);
        using var response = await client.GetAsync("https://assets.upstox.com/market-quote/instruments/exchange/complete.json.gz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(12), client.Timeout);
    }

    [Fact]
    public void FailureDiagnosticsRetainStatusButNeverExceptionPayload()
    {
        Assert.Equal("HTTP 403", UpstoxReplayBackupCapture.FailureDescription(
            new HttpRequestException("sensitive-response", null, HttpStatusCode.Forbidden)));
        Assert.Equal("Transport NameResolutionError", UpstoxReplayBackupCapture.FailureDescription(
            new HttpRequestException(HttpRequestError.NameResolutionError, "sensitive-host")));
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Sarthi-Relay-Key"));
            // Reproduce the observed CDN response: anonymous clients need identification.
            return Task.FromResult(new HttpResponseMessage(request.Headers.UserAgent.ToString() == "Sarthico-Replay/1.0"
                ? HttpStatusCode.OK : HttpStatusCode.Forbidden) { Content = new StringContent("[]") });
        }
    }
}
