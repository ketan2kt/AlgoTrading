using TradingSystem.Domain;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// Contract discovery is shared by the production paper/live decision path,
// capture planning and counterfactual replay. Recording never limits live entries.
public static class RegularOptionContractSelector
{
    public const string Version = "regular-contract-selection-v1";
    public static IReadOnlyList<string> Markets { get; } = Array.AsReadOnly<string>(["NIFTY", "SENSEX", "BANKNIFTY"]);

    public static IReadOnlyList<NiftyOptionContractCandidate> Eligible(
        IReadOnlyCollection<NiftyOptionContractCandidate> contracts, string market,
        DateOnly date, Direction direction, NiftyOptionSelectionOptions? nifty = null)
    {
        var type = direction switch
        {
            Direction.Buy => InstrumentType.CallOption,
            Direction.Sell => InstrumentType.PutOption,
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };
        var settings = nifty ?? new();
        Validate(market, settings);
        var horizon = market switch
        {
            "NIFTY" => settings.MaximumDaysToExpiry,
            // Preserve the production search horizon when rolling same-day Sensex.
            "SENSEX" => contracts.Any(c => c.Type == type && c.ExpiryDate == date &&
                c.TradingSymbol.StartsWith(market, StringComparison.Ordinal)) ? 11 : 10,
            "BANKNIFTY" => 45,
            _ => throw new ArgumentOutOfRangeException(nameof(market))
        };
        var eligible = contracts.Where(c => c.TradingSymbol.StartsWith(market, StringComparison.Ordinal) &&
            c.Type == type && c.ExpiryDate > date && c.ExpiryDate <= date.AddDays(horizon) &&
            c.StrikePrice > 0 && c.LotSize > 0 && c.TickSize > 0).ToArray();
        if (eligible.Length == 0) return [];
        var expiry = eligible.Min(c => c.ExpiryDate);
        return eligible.Where(c => c.ExpiryDate == expiry).ToArray();
    }

    public static NiftyOptionContractCandidate? Select(
        IReadOnlyCollection<NiftyOptionContractCandidate> contracts, string market,
        DateOnly date, Direction direction, decimal price, int inTheMoneySteps = 0,
        NiftyOptionSelectionOptions? nifty = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
        var settings = nifty ?? new();
        var eligible = Eligible(contracts, market, date, direction, settings);
        var step = market == "NIFTY" ? settings.StrikeStep : 100m;
        var anchor = market == "NIFTY"
            ? Math.Round(price / step, MidpointRounding.AwayFromZero) * step : price;
        var desired = anchor + (direction == Direction.Buy ? -1 : 1) * inTheMoneySteps * step;
        var ordered = eligible.OrderBy(c => Math.Abs(c.StrikePrice - desired));
        // Nifty's established tie-break is lower strike; the other markets use symbol.
        return (market == "NIFTY" ? ordered.ThenBy(c => c.StrikePrice) : ordered)
            .ThenBy(c => c.TradingSymbol, StringComparer.Ordinal).FirstOrDefault();
    }

    // Monotone nearest-strike selection: endpoint selections bound every possible
    // contract in this price interval. Include all interior strikes conservatively.
    public static IReadOnlyList<NiftyOptionContractCandidate> RequiredForRange(
        IReadOnlyCollection<NiftyOptionContractCandidate> contracts, string market, DateOnly date,
        decimal low, decimal high, NiftyOptionSelectionOptions? nifty = null)
    {
        if (low <= 0 || high < low) throw new ArgumentOutOfRangeException(nameof(low));
        var settings = nifty ?? new();
        var steps = market switch
        {
            "NIFTY" => new[] { settings.NormalInTheMoneySteps, Math.Max(1, settings.NormalInTheMoneySteps) },
            "SENSEX" => [0, 1],
            _ => [0]
        };
        var required = new List<NiftyOptionContractCandidate>();
        foreach (var direction in new[] { Direction.Buy, Direction.Sell })
        foreach (var offset in steps.Distinct())
        {
            var first = Select(contracts, market, date, direction, low, offset, settings);
            var last = Select(contracts, market, date, direction, high, offset, settings);
            if (first is null || last is null)
                throw new InvalidOperationException(market + ": no eligible " + direction + " contract in the dated master.");
            required.AddRange(Eligible(contracts, market, date, direction, settings).Where(c =>
                c.StrikePrice >= Math.Min(first.StrikePrice, last.StrikePrice) &&
                c.StrikePrice <= Math.Max(first.StrikePrice, last.StrikePrice)));
        }
        return required.DistinctBy(c => c.TradingSymbol).OrderBy(c => c.TradingSymbol, StringComparer.Ordinal).ToArray();
    }

    private static void Validate(string market, NiftyOptionSelectionOptions settings)
    {
        if (!Markets.Contains(market, StringComparer.Ordinal) ||
            settings.MaximumDaysToExpiry is < 1 or > 31 || settings.StrikeStep <= 0)
            throw new ArgumentException("Unsupported regular contract selection settings.");
    }
}
