using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class PaperPriceGeometryPolicyTests
{
    [Theory]
    [InlineData(Direction.Buy, 271.40, 270, 274.20, true)]
    [InlineData(Direction.Buy, 271.40, 270, 270, false)]
    [InlineData(Direction.Sell, 271.40, 273, 268.20, true)]
    [InlineData(Direction.Sell, 271.40, 273, 273, false)]
    public void ValidatesDirectionalProtectivePrices(Direction direction, decimal entry,
        decimal stop, decimal target, bool expected) =>
        Assert.Equal(expected, PaperPriceGeometryPolicy.IsValid(direction, entry, stop, target));
}
