namespace TradingSystem.Application.Broker;

public sealed record GrowwTokenStatus(
    bool IsConfigured,
    bool IsExpired,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    string Source);

public interface IGrowwTokenVault
{
    Task<GrowwTokenStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<string?> GetValidTokenAsync(CancellationToken cancellationToken);

    Task<GrowwTokenStatus> StoreAsync(
        string accessToken,
        string actor,
        CancellationToken cancellationToken);
}
