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
    public void PaperGatewayCannotResolveInBacktestMode()
    {
        using var provider = CreateProvider("Backtest");

        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IBrokerGateway>());
    }

    private static ServiceProvider CreateProvider(string mode)
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
