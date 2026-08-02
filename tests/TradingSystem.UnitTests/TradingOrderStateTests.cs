using TradingSystem.Domain;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class TradingOrderStateTests
{
    [Fact]
    public void ValidLifecycleHandlesPartialAndCompleteFills()
    {
        var order = CreateOrder(10);

        order.Acknowledge("PAPER-1");
        order.RecordFill(4, 100.25m);

        Assert.Equal(OrderState.PartiallyFilled, order.State);
        Assert.Equal(4, order.FilledQuantity);

        order.RecordFill(10, 100.50m);

        Assert.Equal(OrderState.Filled, order.State);
        Assert.Equal(100.50m, order.AverageFillPrice);
    }

    [Fact]
    public void InvalidTransitionFailsClosed()
    {
        var order = CreateOrder(1);

        Assert.Throws<InvalidOperationException>(order.RequestCancellation);
        Assert.Equal(OrderState.ReadyToSubmit, order.State);
    }

    [Fact]
    public void FilledQuantityCannotRegressOrExceedRequest()
    {
        var order = CreateOrder(5);
        order.Acknowledge("PAPER-1");
        order.RecordFill(2, 100m);

        Assert.Throws<ArgumentOutOfRangeException>(() => order.RecordFill(2, 100m));
        Assert.Throws<ArgumentOutOfRangeException>(() => order.RecordFill(6, 100m));
    }

    private static TradingOrder CreateOrder(int quantity) => new(
        Guid.NewGuid(),
        TradingMode.Paper,
        Guid.NewGuid(),
        "client-1",
        quantity,
        OrderState.ReadyToSubmit,
        DateTimeOffset.UtcNow);
}
