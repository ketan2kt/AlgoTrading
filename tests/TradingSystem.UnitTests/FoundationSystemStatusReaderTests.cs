using Microsoft.Extensions.Options;
using TradingSystem.Domain;
using TradingSystem.Infrastructure.SystemStatus;

namespace TradingSystem.UnitTests;

public sealed class FoundationSystemStatusReaderTests
{
    [Fact]
    public void GetCurrentIsFailClosedInPaperMode()
    {
        var reader = new FoundationSystemStatusReader(
            Options.Create(new TradingModeOptions { Mode = TradingMode.Paper }),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 30, 4, 0, 0, TimeSpan.Zero)));

        var result = reader.GetCurrent();

        Assert.Equal(TradingMode.Paper, result.Mode);
        Assert.False(result.LiveTradingAvailable);
        Assert.False(result.TradingEnabled);
        Assert.Equal("FoundationOnly", result.Status);
    }

    [Fact]
    public void GetCurrentRejectsLiveMode()
    {
        var reader = new FoundationSystemStatusReader(
            Options.Create(new TradingModeOptions { Mode = TradingMode.Live }),
            TimeProvider.System);

        Assert.Throws<InvalidOperationException>(reader.GetCurrent);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
