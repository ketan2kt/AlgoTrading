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

public sealed record NiftyOptionSelectionOptions(
    int MaximumDaysToExpiry = 10,
    int ExpiryDayInTheMoneySteps = 1,
    int NormalInTheMoneySteps = 0,
    decimal StrikeStep = 50m);

public static class NiftyOptionContractSelector
{
    public static NiftyOptionContractCandidate? Select(
        IReadOnlyCollection<NiftyOptionContractCandidate> candidates,
        Direction underlyingDirection,
        decimal underlyingPrice,
        DateOnly tradingDate,
        int maximumDaysToExpiry = 10) => Select(candidates, underlyingDirection, underlyingPrice,
            tradingDate, new NiftyOptionSelectionOptions(MaximumDaysToExpiry: maximumDaysToExpiry));

    public static NiftyOptionContractCandidate? Select(
        IReadOnlyCollection<NiftyOptionContractCandidate> candidates,
        Direction underlyingDirection,
        decimal underlyingPrice,
        DateOnly tradingDate,
        NiftyOptionSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(underlyingPrice);
        if (options.MaximumDaysToExpiry is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.StrikeStep);
        var requiredType = underlyingDirection switch
        {
            Direction.Buy => InstrumentType.CallOption,
            Direction.Sell => InstrumentType.PutOption,
            _ => throw new ArgumentOutOfRangeException(nameof(underlyingDirection))
        };

        var eligible = candidates.Where(value =>
                value.Type == requiredType && value.ExpiryDate >= tradingDate &&
                value.ExpiryDate <= tradingDate.AddDays(options.MaximumDaysToExpiry) &&
                value.StrikePrice > 0 && value.LotSize > 0 && value.TickSize > 0)
            .ToArray();
        if (eligible.Length == 0) return null;

        var nearestExpiry = eligible.Min(value => value.ExpiryDate);
        var steps = nearestExpiry == tradingDate
            ? options.ExpiryDayInTheMoneySteps : options.NormalInTheMoneySteps;
        var atm = Math.Round(underlyingPrice / options.StrikeStep,
            MidpointRounding.AwayFromZero) * options.StrikeStep;
        var desiredStrike = requiredType == InstrumentType.CallOption
            ? atm - steps * options.StrikeStep
            : atm + steps * options.StrikeStep;
        return eligible.Where(value => value.ExpiryDate == nearestExpiry)
            .OrderBy(value => Math.Abs(value.StrikePrice - desiredStrike))
            .ThenBy(value => value.StrikePrice)
            .ThenBy(value => value.TradingSymbol, StringComparer.Ordinal)
            .First();
    }
}
