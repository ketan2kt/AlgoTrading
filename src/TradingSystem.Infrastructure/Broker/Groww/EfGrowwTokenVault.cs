using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Broker.Groww;

internal sealed class EfGrowwTokenVault(
    TradingDbContext dbContext,
    IDataProtectionProvider dataProtectionProvider,
    IOptions<GrowwOptions> options,
    TimeProvider timeProvider) : IGrowwTokenVault
{
    private const string ProviderName = "Groww";
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(
        "TradingSystem.Groww.AccessToken.v1");

    public async Task<GrowwTokenStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var secret = await FindAsync(cancellationToken);
        if ((secret is null || secret.ExpiresAtUtc <= timeProvider.GetUtcNow()) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                options.Value.AccessTokenEnvironmentVariable)))
        {
            return new(true, false, null, null, "Environment");
        }

        return ToStatus(secret);
    }

    public async Task<string?> GetValidTokenAsync(CancellationToken cancellationToken)
    {
        var secret = await FindAsync(cancellationToken);
        if (secret is null || secret.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            return null;
        }

        try
        {
            return protector.Unprotect(secret.ProtectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException(
                "The protected Groww token cannot be decrypted; replace it through the administrator screen.");
        }
    }

    public async Task<GrowwTokenStatus> StoreAsync(
        string accessToken,
        string actor,
        CancellationToken cancellationToken)
    {
        accessToken = accessToken?.Trim() ?? string.Empty;
        if (accessToken.Length is < 20 or > 8192 || accessToken.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Groww access token format is invalid.", nameof(accessToken));
        }

        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 160)
        {
            throw new ArgumentException("Authenticated actor is required.", nameof(actor));
        }

        var now = timeProvider.GetUtcNow();
        var expiresAt = NextSixAmIndia(now);
        var protectedValue = protector.Protect(accessToken);
        var secret = await FindAsync(cancellationToken);
        var action = secret is null ? "GrowwTokenCreated" : "GrowwTokenReplaced";
        if (secret is null)
        {
            secret = new BrokerAccessTokenSecret(
                Guid.NewGuid(), ProviderName, protectedValue, expiresAt, actor, now);
            dbContext.BrokerAccessTokenSecrets.Add(secret);
        }
        else
        {
            secret.Replace(protectedValue, expiresAt, actor, now);
        }

        dbContext.AuditLogs.Add(new AuditLog(
            Guid.NewGuid(),
            actor,
            action,
            nameof(BrokerAccessTokenSecret),
            ProviderName,
            "Daily Groww access token updated through the protected administrator workflow.",
            "{}",
            $"{{\"expiresAtUtc\":\"{expiresAt:O}\"}}",
            Guid.NewGuid().ToString("N"),
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToStatus(secret);
    }

    private Task<BrokerAccessTokenSecret?> FindAsync(CancellationToken cancellationToken) =>
        dbContext.BrokerAccessTokenSecrets.SingleOrDefaultAsync(
            value => value.Provider == ProviderName,
            cancellationToken);

    private GrowwTokenStatus ToStatus(BrokerAccessTokenSecret? secret)
    {
        var now = timeProvider.GetUtcNow();
        return secret is null
            ? new(false, false, null, null, "ProtectedDatabase")
            : new(true, secret.ExpiresAtUtc <= now, secret.ExpiresAtUtc,
                secret.UpdatedAtUtc, "ProtectedDatabase");
    }

    private static DateTimeOffset NextSixAmIndia(DateTimeOffset nowUtc)
    {
        var zone = FindIndiaTimeZone();
        var indiaNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var localExpiry = indiaNow.Date.AddHours(6);
        if (indiaNow.DateTime >= localExpiry)
        {
            localExpiry = localExpiry.AddDays(1);
        }

        return new DateTimeOffset(localExpiry, zone.GetUtcOffset(localExpiry)).ToUniversalTime();
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
    }
}
