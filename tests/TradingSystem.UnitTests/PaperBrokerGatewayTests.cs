using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure;

namespace TradingSystem.UnitTests;

public sealed class PaperBrokerGatewayTests
{
    [Fact]
    public async Task DuplicateSubmissionWithSamePayloadIsIdempotent()
    {
        await using var provider = CreateProvider("Paper");
        var gateway = provider.GetRequiredService<IBrokerGateway>();
        var request = Request("same-reference", quantity: 5);

        var first = await gateway.SubmitAsync(request, CancellationToken.None);
        var second = await gateway.SubmitAsync(request, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(OrderState.BrokerAcknowledged, first.State);
    }

    [Fact]
    public async Task DuplicateReferenceWithChangedPayloadIsRejected()
    {
        await using var provider = CreateProvider("Paper");
        var gateway = provider.GetRequiredService<IBrokerGateway>();
        await gateway.SubmitAsync(Request("duplicate", quantity: 5), CancellationToken.None);

        await Assert.ThrowsAsync<DuplicateBrokerOrderException>(() =>
            gateway.SubmitAsync(Request("duplicate", quantity: 6), CancellationToken.None));
    }

    [Fact]
    public async Task PartialFillsBuildPositionAndCompleteDeterministically()
    {
        await using var provider = CreateProvider("Paper");
        var gateway = provider.GetRequiredService<IBrokerGateway>();
        var control = provider.GetRequiredService<IPaperBrokerControl>();
        var instrumentId = Guid.NewGuid();
        await gateway.SubmitAsync(
            Request("partial", 10, instrumentId, maximumFill: 4),
            CancellationToken.None);

        var firstFill = await control.ProcessNextFillAsync("partial", CancellationToken.None);
        var secondFill = await control.ProcessNextFillAsync("partial", CancellationToken.None);
        var finalFill = await control.ProcessNextFillAsync("partial", CancellationToken.None);
        var positions = await gateway.GetPositionsAsync(CancellationToken.None);

        Assert.Equal(4, firstFill.FilledQuantity);
        Assert.Equal(8, secondFill.FilledQuantity);
        Assert.Equal(OrderState.Filled, finalFill.State);
        var position = Assert.Single(positions);
        Assert.Equal(instrumentId, position.InstrumentId);
        Assert.Equal(10, position.Quantity);
        Assert.Equal(22500m, position.AverageEntryPrice);
    }

    [Fact]
    public async Task PartiallyFilledOrderCanCancelOnlyRemainingQuantity()
    {
        await using var provider = CreateProvider("Paper");
        var gateway = provider.GetRequiredService<IBrokerGateway>();
        var control = provider.GetRequiredService<IPaperBrokerControl>();
        await gateway.SubmitAsync(Request("cancel", 10, maximumFill: 3), CancellationToken.None);
        await control.ProcessNextFillAsync("cancel", CancellationToken.None);

        var cancelled = await gateway.CancelAsync("cancel", CancellationToken.None);

        Assert.Equal(OrderState.Cancelled, cancelled.State);
        Assert.Equal(3, cancelled.FilledQuantity);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            control.ProcessNextFillAsync("cancel", CancellationToken.None));
    }

    [Fact]
    public async Task ReconciliationMismatchBlocksTrading()
    {
        await using var provider = CreateProvider("Paper");
        var gateway = provider.GetRequiredService<IBrokerGateway>();

        var result = await gateway.ReconcileAsync(
            [new ExpectedBrokerPosition(Guid.NewGuid(), Direction.Buy, 1)],
            CancellationToken.None);

        Assert.False(result.IsMatched);
        Assert.False(result.TradingPermitted);
        Assert.Single(result.Mismatches);
    }

    [Fact]
    public async Task OpposingFilledOrderClosesPositionLifecycle()
    {
        await using var provider = CreateProvider("Paper");
        var gateway = provider.GetRequiredService<IBrokerGateway>();
        var control = provider.GetRequiredService<IPaperBrokerControl>();
        var instrumentId = Guid.NewGuid();
        await gateway.SubmitAsync(
            Request("entry", 5, instrumentId),
            CancellationToken.None);
        await control.ProcessNextFillAsync("entry", CancellationToken.None);

        await gateway.SubmitAsync(
            Request("exit", 5, instrumentId) with { Direction = Direction.Sell },
            CancellationToken.None);
        await control.ProcessNextFillAsync("exit", CancellationToken.None);

        Assert.Empty(await gateway.GetPositionsAsync(CancellationToken.None));
        var reconciliation = await gateway.ReconcileAsync([], CancellationToken.None);
        Assert.True(reconciliation.IsMatched);
        Assert.True(reconciliation.TradingPermitted);
    }

    [Fact]
    public async Task RestartReconstructsPartialOrderAndPositionAndContinuesDeterministically()
    {
        var journal = new TestPaperBrokerJournal();
        var instrumentId = Guid.NewGuid();
        await using (var firstProvider = CreateProvider("Paper", journal))
        {
            var firstGateway = firstProvider.GetRequiredService<IBrokerGateway>();
            var firstControl = firstProvider.GetRequiredService<IPaperBrokerControl>();
            await firstGateway.SubmitAsync(
                Request("restart-partial", 10, instrumentId, maximumFill: 4),
                CancellationToken.None);
            await firstControl.ProcessNextFillAsync("restart-partial", CancellationToken.None);
        }

        await using var restartedProvider = CreateProvider("Paper", journal);
        var restartedGateway = restartedProvider.GetRequiredService<IBrokerGateway>();
        var restartedControl = restartedProvider.GetRequiredService<IPaperBrokerControl>();

        var restoredOrder = await restartedGateway.GetOrderAsync(
            "restart-partial",
            CancellationToken.None);
        var restoredPosition = Assert.Single(
            await restartedGateway.GetPositionsAsync(CancellationToken.None));
        var secondFill = await restartedControl.ProcessNextFillAsync(
            "restart-partial",
            CancellationToken.None);
        var finalFill = await restartedControl.ProcessNextFillAsync(
            "restart-partial",
            CancellationToken.None);

        Assert.NotNull(restoredOrder);
        Assert.Equal(OrderState.PartiallyFilled, restoredOrder.State);
        Assert.Equal(4, restoredOrder.FilledQuantity);
        Assert.Equal(instrumentId, restoredPosition.InstrumentId);
        Assert.Equal(4, restoredPosition.Quantity);
        Assert.Equal(8, secondFill.FilledQuantity);
        Assert.Equal(OrderState.Filled, finalFill.State);
        Assert.Equal(10, Assert.Single(
            await restartedGateway.GetPositionsAsync(CancellationToken.None)).Quantity);
    }

    [Fact]
    public async Task RestartPreservesIdempotentSubmissionAndOrderSequence()
    {
        var journal = new TestPaperBrokerJournal();
        var request = Request("restart-idempotent", 2);
        BrokerOrderSnapshot original;
        await using (var firstProvider = CreateProvider("Paper", journal))
        {
            original = await firstProvider.GetRequiredService<IBrokerGateway>()
                .SubmitAsync(request, CancellationToken.None);
        }

        await using var restartedProvider = CreateProvider("Paper", journal);
        var gateway = restartedProvider.GetRequiredService<IBrokerGateway>();
        var duplicate = await gateway.SubmitAsync(request, CancellationToken.None);
        var next = await gateway.SubmitAsync(
            Request("next-after-restart", 1),
            CancellationToken.None);

        Assert.Equal(original, duplicate);
        Assert.Equal("PAPER-0000000002", next.BrokerOrderId);
        Assert.Equal(2, journal.Entries.Count(value => value.EventType == "OrderSubmitted"));
    }

    [Fact]
    public async Task JournalFailurePreventsInMemoryOrderMutation()
    {
        var journal = new TestPaperBrokerJournal { FailNextAppend = true };
        await using var provider = CreateProvider("Paper", journal);
        var gateway = provider.GetRequiredService<IBrokerGateway>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.SubmitAsync(Request("journal-failure", 2), CancellationToken.None));

        Assert.Null(await gateway.GetOrderAsync("journal-failure", CancellationToken.None));
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public async Task UnknownJournalCommitOutcomeForcesAuthoritativeReplay()
    {
        var journal = new TestPaperBrokerJournal { CommitThenFailNextAppend = true };
        await using var provider = CreateProvider("Paper", journal);
        var gateway = provider.GetRequiredService<IBrokerGateway>();
        var request = Request("unknown-commit", 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.SubmitAsync(request, CancellationToken.None));

        var recovered = await gateway.GetOrderAsync("unknown-commit", CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal(OrderState.BrokerAcknowledged, recovered.State);
        Assert.Equal("PAPER-0000000001", recovered.BrokerOrderId);
    }

    [Fact]
    public async Task JournalSequenceGapFailsRecoveryClosed()
    {
        var journal = new TestPaperBrokerJournal();
        await journal.AppendAsync(new PaperBrokerJournalEntry(
            Guid.NewGuid(),
            2,
            "OrderSubmitted",
            "gap",
            "{}",
            DateTimeOffset.UtcNow), CancellationToken.None);
        await using var provider = CreateProvider("Paper", journal);
        var gateway = provider.GetRequiredService<IBrokerGateway>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.GetPositionsAsync(CancellationToken.None));

        Assert.Contains("Expected 1, found 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PaperGatewayCannotResolveInBacktestMode()
    {
        using var provider = CreateProvider("Backtest");

        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IBrokerGateway>());
    }

    private static ServiceProvider CreateProvider(
        string mode,
        IPaperBrokerJournal? journal = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:TradingDatabase"] =
                "Host=localhost;Database=unused;Username=unused",
            ["Trading:Mode"] = mode,
            ["IdentityBootstrap:Enabled"] = "false"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(journal ?? new TestPaperBrokerJournal());
        services.AddTradingSystemInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static BrokerOrderRequest Request(
        string clientReference,
        int quantity,
        Guid? instrumentId = null,
        int maximumFill = int.MaxValue) => new(
            clientReference,
            instrumentId ?? Guid.NewGuid(),
            Direction.Buy,
            quantity,
            22500m,
        maximumFill);

}
