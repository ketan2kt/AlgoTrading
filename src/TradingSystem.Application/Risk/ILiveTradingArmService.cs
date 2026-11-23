namespace TradingSystem.Application.Risk;

public interface ILiveTradingArmService
{
    Task<LiveTradingArmStatus> GetAsync(CancellationToken cancellationToken);
    Task<LiveTradingArmStatus> SetAsync(string market, bool armed, string actor,
        CancellationToken cancellationToken);
    Task<LiveTradingArmStatus> SelectBrokerAsync(string market, string broker, string actor,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}

public sealed record LiveMarketArmStatus(string Market, bool Armed, DateTimeOffset? ChangedAtUtc,
    string? ChangedBy, string ExecutionBroker = "Groww");

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
