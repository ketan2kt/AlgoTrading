using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Authorization;
using TradingSystem.Api.Controllers;
using TradingSystem.Api.Hubs;
using TradingSystem.Application.Broker;

namespace TradingSystem.IntegrationTests;

public sealed class SystemStatusEndpointTests :
    IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public SystemStatusEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HTTPS_PORT", "443");
            builder.UseSetting(
                "ConnectionStrings:TradingDatabase",
                "Host=localhost;Database=trading_system_tests;Username=trading_tests");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPaperBrokerJournal>();
                services.AddSingleton<IPaperBrokerJournal>(new TestPaperBrokerJournal());
            });
        }).CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
    }

    [Fact]
    public async Task StatusIsAnonymousAndFailClosed()
    {
        var response = await _client.GetAsync(
            "/api/system/status",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<SystemStatusResponse>(
            CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal("Paper", status.Mode);
        Assert.False(status.LiveTradingAvailable);
        Assert.False(status.TradingEnabled);
    }

    [Fact]
    public async Task CurrentUserRequiresAuthentication()
    {
        var response = await _client.GetAsync(
            "/api/auth/me",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TradingWorkspaceRequiresAuthentication()
    {
        var response = await _client.GetAsync(
            "/api/trading-workspace/nifty",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GrowwTokenVaultRequiresAuthentication()
    {
        var status = await _client.GetAsync(
            "/api/broker/groww/access-token/status",
            CancellationToken.None);
        var store = await _client.PostAsJsonAsync(
            "/api/broker/groww/access-token",
            new { accessToken = new string('x', 40) },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, store.StatusCode);
    }

    [Fact]
    public async Task AntiforgeryEndpointIssuesRequestToken()
    {
        var response = await _client.GetAsync(
            "/api/security/antiforgery-token",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(
            cookies,
            value => value.Contains(
                "__Host-TradingSystem-Antiforgery",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoginWithoutAntiforgeryTokenIsRejectedWithoutServerError()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = "nobody", password = "not-a-real-password" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void TradingWorkspaceAndRealtimeHubRequireAdministratorRole()
    {
        AssertAdministratorOnly(typeof(TradingWorkspaceController));
        AssertAdministratorOnly(typeof(SystemHealthHub));
    }

    private static void AssertAdministratorOnly(Type protectedType)
    {
        var authorization = Assert.Single(
            protectedType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());
        Assert.Equal("Administrator", authorization.Roles);
    }

    private sealed record SystemStatusResponse(
        string Mode,
        bool LiveTradingAvailable,
        bool TradingEnabled);
}
