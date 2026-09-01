using Microsoft.Extensions.Options;
using System.Reflection;
using TradingSystem.Application.SystemStatus;
using TradingSystem.Domain;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.Infrastructure.SystemStatus;

public sealed class FoundationSystemStatusReader(
    IOptions<TradingModeOptions> options,
    IOptions<LiveExecutionOptions> liveOptions,
    TimeProvider timeProvider) : ISystemStatusReader
{
    private static readonly string BuildVersion =
        typeof(FoundationSystemStatusReader).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

    public SystemStatusSnapshot GetCurrent()
    {
        var mode = options.Value.Mode;

        if (mode == TradingMode.Live)
        {
            throw new InvalidOperationException(
                "Live mode is structurally unavailable during the Phase 1 foundation.");
        }

        var liveAvailable = liveOptions.Value.BuildEnabled;
        return new(
            mode,
            LiveTradingAvailable: liveAvailable,
            TradingEnabled: false,
            Status: liveAvailable ? "ControlledLiveAvailable" : "FoundationOnly",
            ObservedAtUtc: timeProvider.GetUtcNow(),
            BuildVersion: BuildVersion);
    }
}
