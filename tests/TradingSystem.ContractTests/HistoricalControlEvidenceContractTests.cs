using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class HistoricalControlEvidenceContractTests
{
    [Theory]
    [InlineData("{\"status\":\"ReconciliationRequired\"}", true)]
    [InlineData("{\"status\":\"Scanning\"}", false)]
    public void ActualMismatchEvidenceIsNotSilentlyReplayedAsHealthy(string json, bool expected) =>
        Assert.Equal(expected, EfHistoricalReplayControlReader.ReconciliationBlocked(json));

    [Theory]
    [InlineData("{}")]
    [InlineData("broken")]
    public void MalformedControlEvidenceIsUnknownRatherThanHealthy(string json) =>
        Assert.Throws<InvalidDataException>(() => EfHistoricalReplayControlReader.ReconciliationBlocked(json));
}
