using TradingSystem.Domain;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record NiftyOptionContractCandidate(
    Guid InstrumentId,
    string TradingSymbol,
    InstrumentType Type,
    DateOnly ExpiryDate,
    decimal StrikePrice,
    int LotSize,
    decimal TickSize);

public static class NiftyOptionContractSelector
{
    public static NiftyOptionContractCandidate? Select(
        IReadOnlyCollection<NiftyOptionContractCandidate> candidates,
        Direction underlyingDirection,
        decimal underlyingPrice,
        DateOnly tradingDate,
        int maximumDaysToExpiry = 10)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(underlyingPrice);
        if (maximumDaysToExpiry is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(maximumDaysToExpiry));
        var requiredType = underlyingDirection switch
        {
            Direction.Buy => InstrumentType.CallOption,
            Direction.Sell => InstrumentType.PutOption,
            _ => throw new ArgumentOutOfRangeException(nameof(underlyingDirection))
        };

        var eligible = candidates.Where(value =>
                value.Type == requiredType && value.ExpiryDate >= tradingDate &&
                value.ExpiryDate <= tradingDate.AddDays(maximumDaysToExpiry) &&
                value.StrikePrice > 0 && value.LotSize > 0 && value.TickSize > 0)
            .ToArray();
        if (eligible.Length == 0) return null;

        var nearestExpiry = eligible.Min(value => value.ExpiryDate);
        return eligible.Where(value => value.ExpiryDate == nearestExpiry)
            .OrderBy(value => Math.Abs(value.StrikePrice - underlyingPrice))
            .ThenBy(value => value.StrikePrice)
            .ThenBy(value => value.TradingSymbol, StringComparer.Ordinal)
            .First();
    }
}
