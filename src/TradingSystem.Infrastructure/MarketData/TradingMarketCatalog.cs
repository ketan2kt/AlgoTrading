using TradingSystem.Domain.Trading;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed record TradingMarketDefinition(
    string Code, string DisplayName, string Exchange, string QuoteSegment,
    string UnderlyingSymbol, InstrumentSegment InstrumentSegment, InstrumentType InstrumentType,
    string ExecutionUnderlying, string ExecutionSegment, bool ExecuteOptions,
    TimeOnly SessionStart, TimeOnly SessionEnd);

internal static class TradingMarketCatalog
{
    public static readonly TradingMarketDefinition Nifty = new("nifty", "Nifty 50", "NSE", "CASH", "NIFTY",
        InstrumentSegment.Cash, InstrumentType.Index, "NIFTY", "FNO", true, new(9, 15), new(15, 30));
    public static readonly TradingMarketDefinition Sensex = new("sensex", "Sensex", "BSE", "CASH", "SENSEX",
        InstrumentSegment.Cash, InstrumentType.Index, "SENSEX", "FNO", true, new(9, 15), new(15, 30));
    public static readonly TradingMarketDefinition NaturalGas = new("natural-gas", "Natural Gas Futures", "MCX",
        "COMMODITY", "NATURALGAS", InstrumentSegment.Commodity, InstrumentType.Future, "NATURALGAS",
        "COMMODITY", false, new(9, 0), new(23, 30));

    public static IReadOnlyList<TradingMarketDefinition> All { get; } = [Nifty, Sensex, NaturalGas];
    public static TradingMarketDefinition Get(string code) => All.FirstOrDefault(value =>
        string.Equals(value.Code, code, StringComparison.OrdinalIgnoreCase)) ??
        throw new ArgumentOutOfRangeException(nameof(code), code, "Unsupported trading market.");
}
