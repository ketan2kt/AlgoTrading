using System.Text.Json;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Strategies;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalNormalPolicyConfiguration(int CandleIntervalSeconds,
    int MinimumSignalCandles, int MinimumFutureCandles, TimeOnly OpeningRangeEnd,
    int NiftyLots, int BankNiftyLots, decimal BankNiftyStopPercent,
    NiftyOptionSelectionOptions NiftySelection, NiftyOptionAdmissionSettings NiftyAdmission,
    HistoricalNiftyExitSettings NiftyExits, HistoricalSensexEntrySettings SensexEntry,
    HistoricalMarketOptionExitSettings MarketExits, MarketRegimeOptions Regime,
    PriceActionStrategyOptions PriceAction, OpeningRangeBreakoutOptions OpeningRange)
{
    public bool WeeklyCarryForwardEnabled { get; init; }
}

// All normal-market adapters are built from one explicit frozen configuration,
// not today's mutable controls or the original day's accepted candidates.
public sealed class HistoricalNormalPolicyFactory
{
    private readonly HistoricalNormalPolicyConfiguration configuration;
    public HistoricalNormalPolicyFactory(HistoricalNormalPolicyConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.WeeklyCarryForwardEnabled)
            throw new ArgumentException("Intraday replay requires carry disabled; an overnight carry must not be silently force-closed.", nameof(configuration));
        // Deep snapshot also detaches configuration option objects from a caller
        // that might reuse them between baseline and proposal requests.
        this.configuration = JsonSerializer.Deserialize<HistoricalNormalPolicyConfiguration>(
            JsonSerializer.Serialize(configuration))!;
        if (configuration.CandleIntervalSeconds is not (60 or 300) ||
            configuration.NiftyExits.CandleIntervalSeconds != configuration.CandleIntervalSeconds ||
            configuration.MarketExits.CandleIntervalSeconds != configuration.CandleIntervalSeconds ||
            configuration.SensexEntry.IntervalSeconds != configuration.CandleIntervalSeconds ||
            configuration.NiftyLots is < 1 or > 100 || configuration.BankNiftyLots is < 1 or > 100 ||
            configuration.BankNiftyStopPercent is <= 0m or > 75m ||
            configuration.SensexEntry.Lots is < 1 or > 100)
            throw new ArgumentException("Invalid frozen normal replay configuration.", nameof(configuration));
    }

    public IHistoricalReplayPortfolioPolicy Create(HistoricalReplayArchiveSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        if (slice.UnderlyingInstrumentId == Guid.Empty)
            throw new ArgumentException("An archived underlying identity is required.", nameof(slice));
        var settings = configuration;
        var contracts = slice.OptionContracts.ToArray();
        var date = slice.Session.Date;
        IHistoricalReplayPortfolioPolicy policy = slice.Session.IndexSymbol switch
        {
            "NIFTY" when slice.PreviousClose is > 0m && slice.FrontFutureSymbol is { Length: > 0 } future =>
                new HistoricalNiftyPositionPolicy(new HistoricalNiftySignalPolicy(
                    slice.UnderlyingInstrumentId, "NIFTY", future, date, slice.PreviousClose.Value,
                    settings.OpeningRangeEnd, settings.MinimumSignalCandles,
                    settings.MinimumFutureCandles, settings.CandleIntervalSeconds, settings.NiftyLots,
                    contracts, settings.NiftySelection, new MarketRegimeEngine(settings.Regime),
                    IndexStrategyPortfolio.Create(settings.PriceAction, settings.OpeningRange),
                    settings.NiftyAdmission), settings.NiftyExits, ticks: contracts.ToDictionary(
                        c => c.TradingSymbol, c => c.TickSize, StringComparer.Ordinal)),
            "SENSEX" when slice.FrontFutureSymbol is { Length: > 0 } future =>
                new HistoricalMarketOptionPositionPolicy(new HistoricalSensexSignalPolicy(
                    slice.UnderlyingInstrumentId, date, future, settings.SensexEntry, contracts, slice.PriorSensexThesis),
                    "sensex", "SENSEX", settings.MarketExits, contracts),
            "BANKNIFTY" => new HistoricalMarketOptionPositionPolicy(new HistoricalBankNiftySignalPolicy(
                slice.UnderlyingInstrumentId, date, settings.CandleIntervalSeconds,
                settings.NiftyAdmission.EntryWindowStart, settings.NiftyAdmission.EntryCutoff,
                settings.BankNiftyLots, contracts, settings.BankNiftyStopPercent,
                settings.NiftyAdmission.MaximumOptionQuoteAgeSeconds, settings.MarketExits.ForcedExit),
                "bank-nifty", "BANKNIFTY", settings.MarketExits, contracts),
            _ => throw new ArgumentException("Required normal-policy market inputs are absent.", nameof(slice))
        };
        var maximumQuoteAge = slice.Session.IndexSymbol == "NIFTY"
            ? settings.NiftyExits.MaximumOptionQuoteAgeSeconds : settings.MarketExits.MaximumOptionAgeSeconds;
        return new HistoricalCompiledPolicyVersion(new HistoricalNormalControlPolicy(policy,
            slice.Session.IndexSymbol, maximumQuoteAge));
    }
}
