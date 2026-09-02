using System.Text.Json;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class LiveExecutionDiscoveryContractTests
{
    [Fact]
    public void ControlledValidationUsesFiveLots()
    {
        var options = new LiveExecutionOptions();

        Assert.Equal(5, options.MaximumLotsPerOrder);
        Assert.Equal(options.MaximumLotsPerOrder, options.ControlledTestLotsPerOrder);
    }

    [Fact]
    public void NiftyDiscoveryUsesDurableFilledEntryReference()
    {
        var signalId = Guid.NewGuid();

        Assert.True(AutomaticLiveExecutionService.TryParseNiftyEntrySignalId(
            "OrderFilled", $"{signalId:N}-ENTRY", out var parsed));
        Assert.Equal(signalId, parsed);
        Assert.False(AutomaticLiveExecutionService.TryParseNiftyEntrySignalId(
            "OrderSubmitted", $"{signalId:N}-ENTRY", out _));
        Assert.False(AutomaticLiveExecutionService.TryParseNiftyEntrySignalId(
            "OrderFilled", $"{signalId:N}-EXIT", out _));
    }

    [Fact]
    public void NiftyDiscoveryReadsCanonicalPaperProposalFields()
    {
        var instrumentId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new
        {
            optionProposal = new
            {
                instrumentId,
                proposedEntry = 100.25m,
                proposedStopLoss = 90.25m,
                proposedTarget = 110.25m
            }
        });

        Assert.True(AutomaticLiveExecutionService.TryReadOptionProposal(json, out var proposal));
        Assert.Equal(instrumentId, proposal.InstrumentId);
        Assert.Equal(100.25m, proposal.EntryPrice);
        Assert.Equal(90.25m, proposal.StopLoss);
        Assert.Equal(110.25m, proposal.Target);
    }

    [Fact]
    public void SensexDiscoveryUsesCanonicalCaseSensitiveMarketCode()
    {
        Assert.True(AutomaticLiveExecutionService.IsSensexPaperSource("sensex", "Active"));
        Assert.False(AutomaticLiveExecutionService.IsSensexPaperSource("SENSEX", "Active"));
        Assert.False(AutomaticLiveExecutionService.IsSensexPaperSource("sensex", "StopLossHit"));
    }

    [Fact]
    public void LiveClientReferenceIsStableForTheSamePaperSource()
    {
        var sourceId = Guid.NewGuid();

        var first = AutomaticLiveExecutionService.LiveClientReference(sourceId);
        var retry = AutomaticLiveExecutionService.LiveClientReference(sourceId);

        Assert.Equal(first, retry);
        Assert.NotEqual(first, AutomaticLiveExecutionService.LiveClientReference(Guid.NewGuid()));
        Assert.True(first.Length <= 20);
    }
}
