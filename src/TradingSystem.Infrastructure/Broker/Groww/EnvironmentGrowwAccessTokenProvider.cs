using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using TradingSystem.Application.Broker;

namespace TradingSystem.Infrastructure.Broker.Groww;

internal sealed class EnvironmentGrowwAccessTokenProvider(
    IOptions<GrowwOptions> options,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) : IGrowwAccessTokenProvider, IDisposable
{
    internal const string AuthClientName = "GrowwAuthenticationApi";
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private DateTimeOffset retryAt;
    private string retryReason = "Token renewal waiting";

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var protectedToken = await GetProtectedTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(protectedToken))
            return protectedToken;

        var groww = options.Value;
        var apiKey = ReadOptionalSecret(groww.TotpApiKeyEnvironmentVariable);
        var totpSecret = ReadOptionalSecret(groww.TotpSecretEnvironmentVariable);
        if (apiKey is not null || totpSecret is not null)
        {
            if (apiKey is null || totpSecret is null)
                throw new InvalidOperationException(
                    "Both Groww TOTP API-key and secret environment variables must be configured.");

            return await GenerateDailyTokenAsync(apiKey, totpSecret, cancellationToken);
        }

        var token = ReadOptionalSecret(groww.AccessTokenEnvironmentVariable);
        if (token is null)
            throw new InvalidOperationException(
                $"Groww access token is unavailable in environment variable '{groww.AccessTokenEnvironmentVariable}'.");

        return token;
    }

    private async Task<string> GenerateDailyTokenAsync(
        string apiKey,
        string totpSecret,
        CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetProtectedTokenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;

            if (timeProvider.GetUtcNow() < retryAt)
                throw new InvalidOperationException($"{retryReason}; next automatic attempt {retryAt:O}.");
            GrowwRenewalClaim claim;
            await using (var reservationScope = scopeFactory.CreateAsyncScope())
                claim = await reservationScope.ServiceProvider.GetRequiredService<IGrowwTokenRenewalGate>()
                    .ClaimAsync(cancellationToken);
            retryAt = claim.NextAttemptAtUtc;
            retryReason = claim.Reason;
            if (claim.Owner is not { } owner)
                throw new InvalidOperationException($"{retryReason}; next automatic attempt {retryAt:O}.");

            int? statusCode = null;
            DateTimeOffset? retryAfter = null;
            var success = false;
            try
            {
            // Another host may have saved the shared token just before our reservation.
            existing = await GetProtectedTokenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(existing)) { success = true; return existing; }

            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/token/api/access");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = JsonContent.Create(new TotpTokenRequest(
                "totp",
                GrowwTotpGenerator.Generate(totpSecret, timeProvider.GetUtcNow())));

            using var response = await httpClientFactory.CreateClient(AuthClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            statusCode = (int)response.StatusCode;
            retryAfter = response.Headers.RetryAfter?.Date ??
                (response.Headers.RetryAfter?.Delta is { } delta ? timeProvider.GetUtcNow() + delta : null);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException(
                    "Groww rejected TOTP authentication. Confirm today's approval on the Groww Cloud API Keys page.");

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Groww TOTP authentication failed with HTTP {(int)response.StatusCode}.");

            var payload = await response.Content.ReadFromJsonAsync<TotpTokenResponse>(
                cancellationToken: cancellationToken);
            var token = payload?.Token?.Trim();
            if (string.IsNullOrWhiteSpace(token) || token.Length is < 20 or > 8192 ||
                token.Any(char.IsWhiteSpace) || payload?.IsActive is false)
                throw new InvalidOperationException(
                    "Groww TOTP authentication returned an invalid access token.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var vault = scope.ServiceProvider.GetRequiredService<IGrowwTokenVault>();
            await vault.StoreAsync(token, "system:groww-totp", cancellationToken);
            success = true;
            return token;
            }
            finally
            {
                var now = timeProvider.GetUtcNow();
                var failure = GrowwTokenRenewalPolicy.Failed(new(retryAt, [], owner, 0, retryReason), now, statusCode, retryAfter);
                retryAt = success ? now : failure.NextAttemptAtUtc;
                retryReason = success ? "Token renewed" : failure.Reason;
                // Persist backoff even when the calling quote cycle timed out.
                using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var completionScope = scopeFactory.CreateAsyncScope();
                await completionScope.ServiceProvider.GetRequiredService<IGrowwTokenRenewalGate>()
                    .CompleteAsync(owner, success, statusCode, retryAfter, completionTimeout.Token);
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task<string?> GetProtectedTokenAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var vault = scope.ServiceProvider.GetRequiredService<IGrowwTokenVault>();
        return await vault.GetValidTokenAsync(cancellationToken);
    }

    private static string? ReadOptionalSecret(string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName))
            throw new InvalidOperationException("A Groww secret environment-variable name is not configured.");

        var value = Environment.GetEnvironmentVariable(variableName)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public void Dispose() => refreshGate.Dispose();

    private sealed record TotpTokenRequest(
        [property: JsonPropertyName("key_type")] string KeyType,
        [property: JsonPropertyName("totp")] string Totp);

    private sealed record TotpTokenResponse(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("isActive")] bool IsActive = true);
}

internal static class GrowwTotpGenerator
{
    internal static string Generate(string base32Secret, DateTimeOffset utcNow)
    {
        var secret = DecodeBase32(base32Secret);
        var counter = utcNow.ToUnixTimeSeconds() / 30;
        Span<byte> counterBytes = stackalloc byte[8];
        for (var index = counterBytes.Length - 1; index >= 0; index--)
        {
            counterBytes[index] = (byte)(counter & 0xff);
            counter >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // RFC 6238/Google Authenticator TOTP requires HMAC-SHA1.
        HMACSHA1.HashData(secret, counterBytes, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24) |
                    ((hash[offset + 1] & 0xff) << 16) |
                    ((hash[offset + 2] & 0xff) << 8) |
                    (hash[offset + 3] & 0xff);
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] DecodeBase32(string value)
    {
        value = value?.Trim().Replace(" ", string.Empty, StringComparison.Ordinal)
            .TrimEnd('=').ToUpperInvariant() ?? string.Empty;
        if (value.Length == 0)
            throw new InvalidOperationException("Groww TOTP secret is empty.");

        var output = new List<byte>(value.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var character in value)
        {
            var digit = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => throw new InvalidOperationException("Groww TOTP secret is not valid Base32.")
            };
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits < 8)
                continue;

            bits -= 8;
            output.Add((byte)(buffer >> bits));
            buffer &= (1 << bits) - 1;
        }

        return output.Count == 0
            ? throw new InvalidOperationException("Groww TOTP secret is too short.")
            : output.ToArray();
    }
}
