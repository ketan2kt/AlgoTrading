using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// Normal Bank Nifty discovery and deferred admission share production policies.
// Executable quote coverage and broker fills remain separate evidence/model concerns.
public sealed class HistoricalBankNiftySignalPolicy(
    Guid underlyingInstrumentId, DateOnly tradingDate, int candleIntervalSeconds,
    TimeOnly entryStart, TimeOnly entryCutoff, int lots,
    IReadOnlyList<NiftyOptionContractCandidate> optionContracts,
    decimal optionStopPercent = 10m,
    int maximumOptionQuoteAgeSeconds = 90,
    TimeOnly? forcedExitTime = null)
    : IHistoricalReplayPortfolioPolicy
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
    public bool ProductionPolicyParityVerified => candleIntervalSeconds is 60 or 300;
    public string CodeVersion => "bank-nifty-deferred-admission-v7/" + OptionRiskBudgetPolicy.Version;
    public string ConfigurationManifest => System.Text.Json.JsonSerializer.Serialize(new
    {
        tradingDate, candleIntervalSeconds, entryStart, entryCutoff, lots,
        optionStopPercent, maximumOptionQuoteAgeSeconds, forcedExitTime,
        Contracts = optionContracts.OrderBy(value => value.TradingSymbol,
            StringComparer.Ordinal).Select(value => new
        {
            value.TradingSymbol, value.Type, value.ExpiryDate,
            value.StrikePrice, value.LotSize, value.TickSize
        }).ToArray()
    });

    public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
        HistoricalReplayPortfolioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var asOf = frame.Market.AvailableAtUtc;
        var local = asOf.ToOffset(IndiaOffset);
        if (DateOnly.FromDateTime(local.DateTime) != tradingDate ||
            !IndexEntryWindowPolicy.AllowsEntry(TimeOnly.FromDateTime(local.DateTime),
                entryStart, entryCutoff) || lots <= 0 || candleIntervalSeconds <= 0 ||
            frame.OpenPosition is not null) return [];
        var session = frame.Market.CompletedBars.Where(value =>
                value.Symbol == "BANKNIFTY" &&
                value.IntervalSeconds == candleIntervalSeconds &&
                DateOnly.FromDateTime(value.OpenTimeUtc.ToOffset(IndiaOffset).DateTime) ==
                    tradingDate)
            .OrderBy(value => value.OpenTimeUtc).ToArray();
        // Production uses the available completed candles and does not add a
        // contiguous-tail strategy gate. Archive completeness is checked separately.
        if (session.Length < 12 || session[^1].AvailableAtUtc != asOf) return [];
        var candles = session.Select(value => new Candle(CandleId(value.OpenTimeUtc),
            underlyingInstrumentId, value.OpenTimeUtc, value.IntervalSeconds,
            value.Open, value.High, value.Low, value.Close, value.Volume,
            "HistoricalReplay", value.OpenInterest)).ToArray();
        var decision = IndexMomentumBreakoutPolicy.Evaluate(candles);
        if (decision.Direction is null) return [];
        var direction = decision.Direction.Value;
        var current = session.Select(ToPriceBar).ToArray();
        var previous = frame.Market.CompletedBars.Where(value =>
                value.Symbol == "BANKNIFTY" &&
                value.IntervalSeconds == candleIntervalSeconds &&
                value.OpenTimeUtc < session[0].OpenTimeUtc)
            .GroupBy(value => DateOnly.FromDateTime(
                value.OpenTimeUtc.ToOffset(IndiaOffset).DateTime))
            .OrderByDescending(value => value.Key).FirstOrDefault()?.OrderBy(value =>
                value.OpenTimeUtc).Select(ToPriceBar).ToArray() ?? [];
        var atr = Atr(session.TakeLast(15).ToArray());
        if (atr <= 0m) return [];
        var opening = session.Take(3).ToArray();
        var openingHigh = opening.Max(value => value.High);
        var openingLow = opening.Min(value => value.Low);
        var preQuote = BankIndexPreQuoteGatePolicy.Evaluate(current, previous,
            direction, atr, openingHigh, openingLow, candleIntervalSeconds);
        if (!preQuote.Permitted) return [];
        var previousExit = frame.ClosedTrades.Where(value => direction == Direction.Buy
                ? value.Symbol.EndsWith("CE", StringComparison.Ordinal)
                : value.Symbol.EndsWith("PE", StringComparison.Ordinal))
            .OrderByDescending(value => value.ExitAtUtc)
            .Select(value => (DateTimeOffset?)value.ExitAtUtc).FirstOrDefault();
        if (!StructuralReentryPolicy.Evaluate(current, direction, previousExit).Permitted) return [];
        if (!OptionOpenInterestEntryPolicy.Evaluate(direction, session[^1].Close,
                atr, frame.LatestOptionChain?.Strikes ?? [],
                frame.PreviousOptionChain?.Strikes ?? []).Permitted) return [];
        var selected = HistoricalBankNiftyOptionSelector.Select(optionContracts,
            tradingDate, direction, session[^1].Close);
        return selected is null || optionStopPercent <= 0m ||
            maximumOptionQuoteAgeSeconds <= 0 ? [] : [new HistoricalReplayCandidate(
            selected.TradingSymbol, asOf, "Bank index directional breakout",
            decision.Confidence, checked(selected.LotSize * lots))
            {
                LotSize = selected.LotSize,
                UnderlyingDirection = direction,
                ContractExpiryDate = selected.ExpiryDate,
                StrategyDecisionAtUtc = asOf,
                RequiresDeferredQuoteAdmission = true,
                MaximumAdmissionQuoteDelaySeconds = maximumOptionQuoteAgeSeconds,
                TriggerOnLastPrice = true,
                PlannedExitAtUtc = new DateTimeOffset(
                    tradingDate.ToDateTime(forcedExitTime ?? new TimeOnly(15, 15)),
                    IndiaOffset).ToUniversalTime()
            }];
    }

    public HistoricalReplayCandidate? AssessDeferredEntry(
        HistoricalReplayPortfolioFrame frame,
        HistoricalReplayCandidate pending,
        HistoricalReplayQuote optionQuote)
    {
        if (!pending.RequiresDeferredQuoteAdmission ||
            !optionQuote.IsFreshAsOf(frame.Market.AvailableAtUtc, TimeSpan.FromSeconds(maximumOptionQuoteAgeSeconds)) ||
            pending.Symbol != optionQuote.Symbol ||
            optionQuote.AvailableAtUtc <= pending.DecidedAtUtc ||
            optionQuote.AvailableAtUtc - pending.DecidedAtUtc >
                TimeSpan.FromSeconds(maximumOptionQuoteAgeSeconds) ||
            frame.OpenPositions.Any(value => value.Symbol == pending.Symbol) ||
            optionQuote.OfferPrice is not > 0m || optionQuote.BidPrice is not > 0m ||
            optionQuote.BidPrice > optionQuote.OfferPrice ||
            optionQuote.LastPrice <= 0m)
            return null;
        var contract = optionContracts.SingleOrDefault(value =>
            value.TradingSymbol == pending.Symbol);
        if (contract is null || contract.TickSize <= 0m) return null;
        var admission = BankIndexOptionAdmissionPolicy.Evaluate(
            optionQuote.LastPrice, optionQuote.BidPrice, optionQuote.OfferPrice,
            contract.LotSize, contract.TickSize, optionStopPercent);
        if (!admission.Permitted) return null;
        var sizing = OptionRiskBudgetPolicy.Size(admission.Entry, admission.Stop,
            contract.LotSize, pending.Quantity / contract.LotSize, "NSE");
        if (sizing.Quantity == 0) return null;
        return pending with
        {
            Quantity = sizing.Quantity,
            DecidedAtUtc = optionQuote.AvailableAtUtc,
            PlannedStopPrice = admission.Stop,
            PlannedTargetPrice = admission.Target,
            RequiresDeferredQuoteAdmission = false
        };
    }

    private static StrategyPriceBar ToPriceBar(HistoricalReplayBar value) =>
        new(value.OpenTimeUtc, value.Open, value.High, value.Low, value.Close);

    private Guid CandleId(DateTimeOffset openTimeUtc)
    {
        var bytes = underlyingInstrumentId.ToByteArray();
        BitConverter.GetBytes(openTimeUtc.UtcTicks).CopyTo(bytes, 0);
        bytes[15] = 1;
        return new Guid(bytes);
    }

    private static decimal Atr(HistoricalReplayBar[] bars)
    {
        if (bars.Length < 2) return 0m;
        var total = 0m;
        for (var index = 1; index < bars.Length; index++)
            total += Math.Max(bars[index].High - bars[index].Low,
                Math.Max(Math.Abs(bars[index].High - bars[index - 1].Close),
                    Math.Abs(bars[index].Low - bars[index - 1].Close)));
        return total / (bars.Length - 1);
    }
}

public static class HistoricalBankNiftyOptionSelector
{
    // Mirrors the production bank-index expiry and nearest-strike ordering;
    // candidates still require independently verified historical lot metadata.
    public static NiftyOptionContractCandidate? Select(
        IReadOnlyList<NiftyOptionContractCandidate> contracts,
        DateOnly tradingDate, Direction direction, decimal underlyingPrice)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        if (direction is not (Direction.Buy or Direction.Sell) ||
            underlyingPrice <= 0m) throw new ArgumentOutOfRangeException(nameof(direction));
        return RegularOptionContractSelector.Select(contracts, "BANKNIFTY", tradingDate, direction, underlyingPrice);
    }
}
