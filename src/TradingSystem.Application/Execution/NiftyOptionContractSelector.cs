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
        return RegularOptionContractSelector.Select(candidates, "NIFTY", tradingDate,
            underlyingDirection, underlyingPrice, options.NormalInTheMoneySteps, options);
    }
}
