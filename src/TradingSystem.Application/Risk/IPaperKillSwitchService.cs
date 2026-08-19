namespace TradingSystem.Application.Risk;

public sealed record PaperKillSwitchStatus(bool Active, DateTimeOffset? UpdatedAtUtc);

public interface IPaperKillSwitchService
{
    Task<PaperKillSwitchStatus> GetAsync(CancellationToken cancellationToken);
    Task<PaperKillSwitchStatus> SetAsync(bool active, string reason, string actor,
        CancellationToken cancellationToken);
}

public sealed record PaperDailyLossOverrideStatus(
    bool Active,
    DateOnly SessionDate,
    DateTimeOffset? UpdatedAtUtc);

public interface IPaperDailyLossOverrideService
{
    Task<PaperDailyLossOverrideStatus> GetAsync(CancellationToken cancellationToken);
    Task<PaperDailyLossOverrideStatus> SetAsync(bool active, string reason, string actor,
        CancellationToken cancellationToken);
}
