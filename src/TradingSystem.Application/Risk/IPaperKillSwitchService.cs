namespace TradingSystem.Application.Risk;

public sealed record PaperKillSwitchStatus(bool Active, DateTimeOffset? UpdatedAtUtc);

public interface IPaperKillSwitchService
{
    Task<PaperKillSwitchStatus> GetAsync(CancellationToken cancellationToken);
    Task<PaperKillSwitchStatus> SetAsync(bool active, string reason, string actor,
        CancellationToken cancellationToken);
}
