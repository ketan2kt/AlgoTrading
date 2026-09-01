namespace TradingSystem.Application.Risk;

public interface ILiveTradingArmService
{
    Task<LiveTradingArmStatus> GetAsync(CancellationToken cancellationToken);
    Task<LiveTradingArmStatus> SetAsync(bool armed, string reason, string actor,
        CancellationToken cancellationToken);
}

public sealed record LiveTradingArmStatus(
    bool BuildEnabled,
    bool Armed,
    DateOnly? ArmedForTradingDate,
    int MaximumLotsPerOrder,
    bool ControlledBrokerTestCompleted,
    string[] AllowedMarkets,
    DateTimeOffset? ChangedAtUtc,
    string? ChangedBy);
