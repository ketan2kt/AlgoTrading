using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class TradingWorkspaceReasonParsingTests
{
    [Fact]
    public void ParsesLegacyObjectWithoutCrashingWorkspace()
    {
        var reasons = EfTradingWorkspaceReader.ParseRejectionReasons(
            "{\"trend\":\"bearish\",\"relativeVolume\":0.68}");

        Assert.Equal(["trend: bearish", "relativeVolume: 0.68"], reasons);
    }

    [Fact]
    public void ParsesCurrentArrayFormat()
    {
        var reasons = EfTradingWorkspaceReader.ParseRejectionReasons(
            "[\"Market regime blocked\",\"Volume below threshold\"]");

        Assert.Equal(["Market regime blocked", "Volume below threshold"], reasons);
    }

    [Fact]
    public void PreservesLegacyPlainText()
    {
        var reasons = EfTradingWorkspaceReader.ParseRejectionReasons("Waiting for structure");

        Assert.Equal(["Waiting for structure"], reasons);
    }
}
