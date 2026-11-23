using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;

namespace TradingSystem.IntegrationTests;

public sealed class UpstoxOAuthReturnTests : IDisposable
{
    private readonly Authorization authorization = new();
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    private const string Nonce = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public UpstoxOAuthReturnTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HTTPS_PORT", "443");
            builder.UseSetting("ConnectionStrings:TradingDatabase", "Host=localhost;Database=trading_system_tests;Username=trading_tests");
            builder.ConfigureTestServices(services =>
            {
                foreach (var worker in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                    (s.ImplementationFactory is not null || s.ImplementationType?.Namespace?.StartsWith("TradingSystem.", StringComparison.Ordinal) == true)).ToArray())
                    services.Remove(worker);
                services.RemoveAll<IUpstoxExecutionAuthorization>();
                services.AddSingleton<IUpstoxExecutionAuthorization>(authorization);
            });
        });
        client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
    }

    public void Dispose() { client.Dispose(); factory.Dispose(); }

    [Fact]
    public async Task CrossSiteReturnWithoutStrictAdminCookieOnlyRendersSameSiteBridge()
    {
        using var request = Request("callback", State(), includeNonce: true);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("action=\"/api/upstox-execution/complete\"", html, StringComparison.Ordinal);
        Assert.Contains("Complete secure connection", html, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(0, authorization.Completions);
        Assert.Equal(Microsoft.AspNetCore.Http.SameSiteMode.Strict, CookieOptions().Cookie.SameSite);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task MissingNonceOrTamperedStateCannotRenderBridge(bool includeNonce, bool tamper)
    {
        using var request = Request("callback", tamper ? "invalid-state" : State(), includeNonce);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, authorization.Completions);
    }

    [Fact]
    public async Task AnonymousCompletionStillRequiresAdministratorAuthentication()
    {
        using var response = await client.SendAsync(Request("complete", State(), true));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, authorization.Completions);
    }

    [Fact]
    public async Task ExpiredStateCannotRenderBridge()
    {
        var state = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("UpstoxExecutionOAuthState.v1").ToTimeLimitedDataProtector()
            .Protect("admin|" + Nonce, DateTimeOffset.UtcNow.AddMinutes(-1));
        using var response = await client.SendAsync(Request("callback", state, true));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, authorization.Completions);
    }

    [Theory]
    [InlineData("other-admin", "Administrator", HttpStatusCode.BadRequest)]
    [InlineData("admin", "Viewer", HttpStatusCode.Forbidden)]
    [InlineData("admin", "Administrator", HttpStatusCode.Redirect)]
    public async Task SameSiteCompletionRequiresOriginalAdministrator(string name, string role, HttpStatusCode expected)
    {
        using var request = Request("complete", State(), true, AdminCookie(name, role));
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected == HttpStatusCode.Redirect ? 1 : 0, authorization.Completions);
        if (expected == HttpStatusCode.Redirect)
        {
            Assert.Equal("admin", authorization.Actor);
            Assert.Equal("/nifty?executionConnected=Upstox", response.Headers.Location!.OriginalString);
            Assert.Contains(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith("__Host-upstox-execution-state=;", StringComparison.Ordinal));
        }
    }

    private string State() => factory.Services.GetRequiredService<IDataProtectionProvider>()
        .CreateProtector("UpstoxExecutionOAuthState.v1").ToTimeLimitedDataProtector()
        .Protect("admin|" + Nonce, TimeSpan.FromMinutes(10));
    private CookieAuthenticationOptions CookieOptions() => factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
        .Get(IdentityConstants.ApplicationScheme);
    private string AdminCookie(string name, string role)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role) }, IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
            { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) }, IdentityConstants.ApplicationScheme);
        return "__Host-TradingSystem=" + CookieOptions().TicketDataFormat.Protect(ticket);
    }
    private static HttpRequestMessage Request(string path, string state, bool includeNonce, string? admin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/upstox-execution/" + path + "?code=test-code&state=" + Uri.EscapeDataString(state));
        var cookies = new List<string>();
        if (includeNonce) cookies.Add("__Host-upstox-execution-state=" + Nonce);
        if (admin is not null) cookies.Add(admin);
        if (cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookies));
        return request;
    }
    private sealed class Authorization : IUpstoxExecutionAuthorization
    {
        public int Completions { get; private set; }
        public string? Actor { get; private set; }
        public string AuthorizationUrl(string state) => throw new NotSupportedException();
        public Task<UpstoxExecutionReadiness> GetStatusAsync(CancellationToken token) => throw new NotSupportedException();
        public Task CompleteAsync(string code, string actor, CancellationToken token)
        { Completions++; Actor = actor; return Task.CompletedTask; }
    }
}
