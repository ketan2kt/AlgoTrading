using Microsoft.Extensions.Options;
using TradingSystem.Application.SystemStatus;
using TradingSystem.Domain;

namespace TradingSystem.Infrastructure.SystemStatus;

public sealed class FoundationSystemStatusReader(
    IOptions<TradingModeOptions> options,
    TimeProvider timeProvider) : ISystemStatusReader
{
    public SystemStatusSnapshot GetCurrent()
    {
        var mode = options.Value.Mode;

        if (mode == TradingMode.Live)
        {
            throw new InvalidOperationException(
                "Live mode is structurally unavailable during the Phase 1 foundation.");
        }

        return new(
            mode,
            LiveTradingAvailable: false,
            TradingEnabled: false,
            Status: "FoundationOnly",
            ObservedAtUtc: timeProvider.GetUtcNow());
    }
}

