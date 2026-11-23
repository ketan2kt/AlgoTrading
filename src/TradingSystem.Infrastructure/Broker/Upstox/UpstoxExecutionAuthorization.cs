using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Broker.Upstox;

internal sealed class UpstoxExecutionAuthorization(TradingDbContext db, IDataProtectionProvider protection,
    IOptions<UpstoxExecutionOptions> settings, IHttpClientFactory clients, TimeProvider clock)
    : IUpstoxExecutionAuthorization, IUpstoxExecutionCredentialProvider
{
    internal const string Provider = "UpstoxExecution";
    internal const string AuthClient = "UpstoxExecutionAuth";
    private readonly IDataProtector protector = protection.CreateProtector("Sarthico.Upstox.Execution.v1");
    private UpstoxExecutionOptions Options => settings.Value;
    private bool Configured => Options.Enabled && !string.IsNullOrWhiteSpace(Options.ClientId) &&
        !string.IsNullOrWhiteSpace(Options.ClientSecret) && !string.IsNullOrWhiteSpace(Options.AccountId);

    public string AuthorizationUrl(string state)
    {
        if (!Configured || Options.Sandbox) throw new InvalidOperationException("Upstox execution app/account is not configured for authorization.");
        return "https://api.upstox.com/v2/login/authorization/dialog?response_type=code&client_id=" +
            Uri.EscapeDataString(Options.ClientId) + "&redirect_uri=" + Uri.EscapeDataString(Options.RedirectUri) +
            "&state=" + Uri.EscapeDataString(state);
    }

    public async Task CompleteAsync(string code, string actor, CancellationToken token)
    {
        if (!Configured || string.IsNullOrWhiteSpace(code) || code.Length > 2048)
            throw new InvalidOperationException("Upstox authorization is not ready.");
        // No retries: authorization codes are single-use. Neither body nor response is logged.
        using var response = await clients.CreateClient(AuthClient).PostAsync("https://api.upstox.com/v2/login/authorization/token",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["code"] = code, ["client_id"] = Options.ClientId, ["client_secret"] = Options.ClientSecret,
                ["redirect_uri"] = Options.RedirectUri, ["grant_type"] = "authorization_code"
            }), token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Upstox authorization exchange failed; reconnect without reusing the code.");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var root = document.RootElement;
        if (root.GetProperty("user_id").GetString() != Options.AccountId ||
            !root.GetProperty("is_active").GetBoolean())
            throw new InvalidOperationException("Upstox authorized account does not match the configured account.");
        var access = root.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(access) || access.Length > 16384) throw new InvalidDataException("Missing execution credential.");
        var exchanges = root.GetProperty("exchanges").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var products = root.GetProperty("products").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (!products.Contains("D") || !exchanges.Any(x => x is "NFO" or "BFO"))
            throw new InvalidOperationException("Upstox derivatives product is not enabled.");
        var now = clock.GetUtcNow(); var expiry = NextExpiry(now);
        var value = protector.Protect(JsonSerializer.Serialize(new Credential(access, Options.AccountId, exchanges, Options.Sandbox)));
        var existing = await db.BrokerAccessTokenSecrets.SingleOrDefaultAsync(x => x.Provider == Provider, token);
        if (existing is null) db.BrokerAccessTokenSecrets.Add(new(Guid.NewGuid(), Provider, value, expiry, actor, now));
        else existing.Replace(value, expiry, actor, now);
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor, "UpstoxExecutionAuthorized", "BrokerAccessTokenSecret", Provider,
            "Execution authorization updated; no markets armed and no order submitted.", "{}",
            JsonSerializer.Serialize(new { expiresAtUtc = expiry }), Guid.NewGuid().ToString("N"), now));
        await db.SaveChangesAsync(token);
    }

    public async Task<Credential> ReadAsync(CancellationToken token)
    {
        var secret = await db.BrokerAccessTokenSecrets.AsNoTracking().SingleOrDefaultAsync(x => x.Provider == Provider, token);
        if (!Options.Enabled || secret is null || secret.ExpiresAtUtc <= clock.GetUtcNow())
            throw new InvalidOperationException("Upstox trading authorization is missing or expired; reconnect in Controls.");
        Credential? saved;
        try { saved = JsonSerializer.Deserialize<Credential>(protector.Unprotect(secret.ProtectedValue)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        { throw new InvalidOperationException("Upstox trading authorization cannot be read securely."); }
        if (saved is null || saved.Account != Options.AccountId || saved.Sandbox != Options.Sandbox)
            throw new InvalidOperationException("Upstox credential account/environment differs from execution configuration.");
        return saved;
    }

    public async Task<UpstoxExecutionReadiness> GetStatusAsync(CancellationToken token)
    {
        var secret = await db.BrokerAccessTokenSecrets.AsNoTracking().SingleOrDefaultAsync(x => x.Provider == Provider, token);
        try
        {
            var value = await ReadAsync(token);
            return new(Configured, true, Options.StaticIpVerified, Options.Sandbox, secret?.ExpiresAtUtc, value.Exchanges,
                Options.StaticIpVerified ? "Trading authorization present; broker acceptance still requires verification." : "Execution static IP needs verification.");
        }
        catch (InvalidOperationException)
        { return new(Configured, false, Options.StaticIpVerified, Options.Sandbox, secret?.ExpiresAtUtc, [], "Connect Upstox trading authorization before enabling new entries."); }
    }

    internal static DateTimeOffset NextExpiry(DateTimeOffset now)
    {
        var india = now.ToOffset(TimeSpan.FromMinutes(330));
        var expiry = new DateTimeOffset(india.Date.AddHours(3.5), india.Offset);
        return (expiry <= now ? expiry.AddDays(1) : expiry).ToUniversalTime();
    }
    internal sealed record Credential(string AccessToken, string Account, string[] Exchanges, bool Sandbox);
}

internal interface IUpstoxExecutionCredentialProvider
{
    Task<UpstoxExecutionAuthorization.Credential> ReadAsync(CancellationToken token);
}
