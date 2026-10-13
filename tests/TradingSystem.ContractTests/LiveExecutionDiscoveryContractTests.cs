using System.Text.Json;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class LiveExecutionDiscoveryContractTests
{
    [Fact]
    public void PaperPromotionIsRequiredUnlessExplicitlyDisabled()
    {
        Assert.True(new LiveExecutionOptions().RequirePaperPromotion);
    }

    [Theory]
    [InlineData("nifty")]
    [InlineData("sensex")]
    public async Task DisabledPromotionDoesNotRequireAResearchReport(string market)
    {
        // A null database proves the override does not read or require research data.
        Assert.True(await AutomaticLiveExecutionService.IsPaperPromotionSatisfiedAsync(
            null!, market, false, CancellationToken.None));
    }

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
        Assert.False(AutomaticLiveExecutionService.IsSensexPaperSource("sensex", "Active",
            "Research|Index momentum breakout"));
        Assert.True(AutomaticLiveExecutionService.IsSensexPaperSource("sensex", "Active",
            "Research|Index momentum breakout", allowResearchSources: true));
    }

    [Fact]
    public void NiftyResearchSignalsCanNeverEnterLiveDiscovery()
    {
        Assert.True(AutomaticLiveExecutionService.IsNiftyPaperSource("ema-pullback"));
        Assert.False(AutomaticLiveExecutionService.IsNiftyPaperSource(
            "Research|nifty-structure-retest"));
        Assert.True(AutomaticLiveExecutionService.IsNiftyPaperSource(
            "Research|nifty-structure-retest", allowResearchSources: true));
        Assert.False(AutomaticLiveExecutionService.IsNiftyPaperSource(null));
    }

    [Fact]
    public void ReconciliationIgnoresUnmanagedBrokerPositions()
    {
        var unmanagedInstrument = Guid.NewGuid();

        var result = AutomaticLiveExecutionService.ReconcileManagedPositions([],
            [new BrokerPositionSnapshot(unmanagedInstrument, Direction.Buy, 650, 100m)],
            DateTimeOffset.UtcNow);

        Assert.True(result.TradingPermitted);
        Assert.Empty(result.Mismatches);
    }

    [Fact]
    public void ReconciliationStillBlocksMismatchInManagedInstrument()
    {
        var managedInstrument = Guid.NewGuid();
        var result = AutomaticLiveExecutionService.ReconcileManagedPositions(
            [new ExpectedBrokerPosition(managedInstrument, Direction.Buy, 100)],
            [new BrokerPositionSnapshot(managedInstrument, Direction.Buy, 150, 100m)],
            DateTimeOffset.UtcNow);

        Assert.False(result.TradingPermitted);
        Assert.Single(result.Mismatches);
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

    [Fact]
    public void DefiniteUnauthorisedPreSubmissionAttemptDoesNotBlockLaterTrades()
    {
        var intent = new LiveExecutionIntent(Guid.NewGuid(), "SENSEX", Guid.NewGuid(), "SENSEX",
            Guid.NewGuid(), Direction.Buy, 100, 100m, 90m, 110m, "LE12345678",
            DateTimeOffset.UtcNow);
        intent.RequireReconciliation("Groww execution API returned 401.");

        Assert.True(AutomaticLiveExecutionService.IsDefinitivePreSubmissionRejection(intent));
    }
}
