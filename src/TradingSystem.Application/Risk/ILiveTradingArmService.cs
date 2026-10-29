namespace TradingSystem.Application.Risk;

public interface ILiveTradingArmService
{
    Task<LiveTradingArmStatus> GetAsync(CancellationToken cancellationToken);
    Task<LiveTradingArmStatus> SetAsync(string market, bool armed, string actor,
        CancellationToken cancellationToken);
}

public sealed record LiveMarketArmStatus(string Market, bool Armed, DateTimeOffset? ChangedAtUtc,
    string? ChangedBy);

public sealed record LiveTradingArmStatus(
    bool BuildEnabled,
    IReadOnlyList<LiveMarketArmStatus> Markets,
    bool MirrorsPaperQuantity,
    bool ControlledBrokerTestCompleted,
    string[] AllowedMarkets,
    string? LatestIntentMarket,
    string? LatestIntentStatus,
    string? LatestIntentError,
    DateTimeOffset? LatestIntentAtUtc)
{
    public LiveMarketArmStatus? ForMarket(string market) =>
        Markets.FirstOrDefault(value => value.Market == market);
}
