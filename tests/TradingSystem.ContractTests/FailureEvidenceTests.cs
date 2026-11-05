using System.Text.Json;
using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.ContractTests;

public sealed class FailureEvidenceTests
{
    [Fact]
    public void DurableFailureEvidenceNeverCopiesExceptionMessagesOrSecrets()
    {
        var failure = new InvalidOperationException("password=secret; customer data; SQL text");
        var json = JsonSerializer.Serialize(TradingEngineEvidenceRecorder.FailureEvidence(failure));
        Assert.Contains("InvalidOperationException", json);
        Assert.Contains("frames", json);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("SQL", json);
    }
}
