using Microsoft.Extensions.Options;
using TradingSystem.Domain;
using TradingSystem.Infrastructure.SystemStatus;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.UnitTests;

public sealed class FoundationSystemStatusReaderTests
{
    [Fact]
    public void GetCurrentIsFailClosedInPaperMode()
    {
        var reader = new FoundationSystemStatusReader(
            Options.Create(new TradingModeOptions { Mode = TradingMode.Paper }),
            Options.Create(new LiveExecutionOptions()),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 30, 4, 0, 0, TimeSpan.Zero)));

        var result = reader.GetCurrent();

        Assert.Equal(TradingMode.Paper, result.Mode);
        Assert.False(result.LiveTradingAvailable);
        Assert.False(result.TradingEnabled);
        Assert.Equal("FoundationOnly", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.BuildVersion));
    }

    [Fact]
    public void GetCurrentRejectsLiveMode()
    {
        var reader = new FoundationSystemStatusReader(
            Options.Create(new TradingModeOptions { Mode = TradingMode.Live }),
            Options.Create(new LiveExecutionOptions()),
            TimeProvider.System);

        Assert.Throws<InvalidOperationException>(reader.GetCurrent);
    }

    [Fact]
    public void GetCurrentAdvertisesControlledLiveBuildWithoutClaimingItIsArmed()
    {
        var reader = new FoundationSystemStatusReader(
            Options.Create(new TradingModeOptions { Mode = TradingMode.Paper }),
            Options.Create(new LiveExecutionOptions { BuildEnabled = true }),
            TimeProvider.System);

        var result = reader.GetCurrent();

        Assert.True(result.LiveTradingAvailable);
        Assert.False(result.TradingEnabled);
        Assert.Equal("ControlledLiveAvailable", result.Status);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
