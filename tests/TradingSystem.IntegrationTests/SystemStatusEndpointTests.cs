using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

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

    private sealed record SystemStatusResponse(
        string Mode,
        bool LiveTradingAvailable,
        bool TradingEnabled);
}
