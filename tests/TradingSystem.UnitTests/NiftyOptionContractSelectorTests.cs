using TradingSystem.Application.Execution;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class NiftyOptionContractSelectorTests
{
    private static readonly DateOnly Today = new(2026, 8, 3);

    [Theory]
    [InlineData(Direction.Buy, InstrumentType.CallOption)]
    [InlineData(Direction.Sell, InstrumentType.PutOption)]
    public void SelectsNearestExpiryAtmContractForUnderlyingDirection(
        Direction direction,
        InstrumentType expectedType)
    {
        var result = NiftyOptionContractSelector.Select([
            Option(InstrumentType.CallOption, Today.AddDays(3), 25000),
            Option(InstrumentType.PutOption, Today.AddDays(3), 25000),
            Option(expectedType, Today.AddDays(3), 25100),
            Option(expectedType, Today.AddDays(10), 25050)
        ], direction, 25040m, Today);

        Assert.NotNull(result);
        Assert.Equal(expectedType, result.Type);
        Assert.Equal(Today.AddDays(3), result.ExpiryDate);
        Assert.Equal(25000m, result.StrikePrice);
    }

    [Fact]
    public void RejectsExpiredOrIncompleteContracts()
    {
        var result = NiftyOptionContractSelector.Select([
            Option(InstrumentType.CallOption, Today.AddDays(-1), 25000),
            Option(InstrumentType.CallOption, Today.AddDays(3), 25000) with { LotSize = 0 },
            Option(InstrumentType.CallOption, Today.AddDays(20), 25000)
        ], Direction.Buy, 25000m, Today);

        Assert.Null(result);
    }

    private static NiftyOptionContractCandidate Option(
        InstrumentType type,
        DateOnly expiry,
        decimal strike) =>
        new(Guid.NewGuid(), $"NIFTY-{expiry:yyyyMMdd}-{strike}-{type}", type,
            expiry, strike, 75, 0.05m);
}
