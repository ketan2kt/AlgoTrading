using System.Text.Json;
using TradingSystem.Application.Broker;
using TradingSystem.Application.MarketData;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalSensexEntrySettings(TimeOnly EntryStart, TimeOnly EntryCutoff,
    TimeOnly ForcedExit, int Lots, int IntervalSeconds, decimal StopPercent,
    int MaximumOptionAgeSeconds);

public sealed record HistoricalSensexSignalDecision(
    IReadOnlyList<HistoricalReplayCandidate> Candidates, string Outcome);

// Counterfactual normal Sensex discovery on an as-of clock. Every mathematical
// policy is the production policy; no original trade is used as a candidate.
// The prior-session thesis must be supplied when it existed in production.
public sealed class HistoricalSensexSignalPolicy(Guid underlyingInstrumentId,
    DateOnly tradingDate, string futuresSymbol, HistoricalSensexEntrySettings settings,
    IReadOnlyList<NiftyOptionContractCandidate> contracts,
    PersistentDirectionalThesisV1? priorSessionThesis = null) : IHistoricalReplayPortfolioPolicy
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
    public bool ProductionPolicyParityVerified => settings.IntervalSeconds is 60 or 300;
    public string CodeVersion => "sensex-normal-entry-v2";
    public string ConfigurationManifest => JsonSerializer.Serialize(new
    {
        underlyingInstrumentId, tradingDate, futuresSymbol, settings, priorSessionThesis,
        Contracts = contracts.OrderBy(value => value.TradingSymbol, StringComparer.Ordinal)
    });

    public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) =>
        EvaluateDetailed(frame).Candidates;

    public HistoricalSensexSignalDecision EvaluateDetailed(HistoricalReplayPortfolioFrame frame)
    {
        var now = frame.Market.AvailableAtUtc;
        var local = now.ToOffset(IndiaOffset);
        var time = TimeOnly.FromDateTime(local.DateTime);
        if (DateOnly.FromDateTime(local.DateTime) != tradingDate || settings.Lots <= 0 ||
            !IndexEntryWindowPolicy.AllowsEntry(time, settings.EntryStart, settings.EntryCutoff)) return new([], "EntryWindow");
        var session = frame.Market.CompletedBars.Where(value => value.Symbol == "SENSEX" &&
                value.IntervalSeconds == settings.IntervalSeconds && value.AvailableAtUtc <= now &&
                DateOnly.FromDateTime(value.OpenTimeUtc.ToOffset(IndiaOffset).DateTime) == tradingDate)
            .OrderBy(value => value.OpenTimeUtc).ToArray();
        if (session.Length < 12 || session[^1].AvailableAtUtc != now) return new([], "CompletedCandles");
        var bars = session.Select(ToBar).ToArray();
        var futures = frame.Market.CompletedBars.Where(value => value.Symbol == futuresSymbol &&
                value.IntervalSeconds == settings.IntervalSeconds && value.AvailableAtUtc <= now &&
                DateOnly.FromDateTime(value.OpenTimeUtc.ToOffset(IndiaOffset).DateTime) == tradingDate)
            .OrderBy(value => value.OpenTimeUtc).ToArray();
        var confirmation = Confirmation(session, futures);
        if (!confirmation.Ready) return new([], "FuturesConfirmation");
        var fast = TechnicalIndicators.ExponentialMovingAverage(bars.Select(value => value.Close).ToArray(), 9);
        var slow = TechnicalIndicators.ExponentialMovingAverage(bars.Select(value => value.Close).ToArray(),
            Math.Min(21, bars.Length));
        var state = MarketStateRouterV2.Analyze(bars, confirmation.Vwap, fast, slow);
        var lifecycle = SensexSetupLifecyclePolicy.Evaluate(bars);
        var momentum = IndexMomentumBreakoutPolicy.Evaluate(session.Select(value =>
            new Candle(CandleId(value.OpenTimeUtc), underlyingInstrumentId, value.OpenTimeUtc,
                value.IntervalSeconds, value.Open, value.High, value.Low, value.Close,
                value.Volume, "HistoricalReplay", value.OpenInterest)).ToArray());
        // Reconstruct from prior completed frames rather than holding mutable
        // state on the policy instance, which would leak across comparison runs.
        var thesis = priorSessionThesis;
        for (var count = 12; count <= session.Length; count++)
        {
            var prefix = session.Take(count).ToArray();
            var opening = prefix.Take(3).ToArray();
            thesis = PersistentDirectionalThesisAnalyzerV1.Analyze(prefix.Select(ToBar).ToArray(),
                Confirmation(prefix, futures).Vwap, opening.Max(value => value.High),
                opening.Min(value => value.Low), thesis);
        }
        var selection = SensexEntrySelectionPolicy.Evaluate(new(momentum.Direction,
            momentum.Confidence, "Index momentum breakout", momentum.Reasons), bars,
            lifecycle, thesis, state, confirmation.Vwap, fast, slow, frame.OpenPositions.Count > 0);
        if (selection.Candidate.Direction is not { } direction) return new([], "NoSignal");
        if (!selection.Supplemental && (lifecycle.Direction != direction ||
            !SensexSetupLifecyclePolicy.AllowsChampionEntry(lifecycle))) return new([], "SetupLifecycle");
        var atr = Atr(session.TakeLast(15).ToArray());
        if (atr <= 0m) return new([], "AtrUnavailable");
        var adaptive = SensexAdaptiveSetupPolicy.Assess(bars, direction, atr,
            Math.Abs(bars[0].Open - bars[0].Close) > atr);
        if (!selection.Supplemental && !SensexAdaptiveSetupPolicy.AllowsMomentumEntry(adaptive)) return new([], "AdaptiveSetup");
        var openingBars = bars.Take(3).ToArray();
        var high = openingBars.Max(value => value.High);
        var low = openingBars.Min(value => value.Low);
        var structure = MarketStructureQualityAnalyzer.Analyze(bars, direction, bars[^1].Close,
            confirmation.Vwap, atr / bars[^1].Close * 100m, high, low);
        var previous = frame.Market.CompletedBars.Where(value => value.Symbol == "SENSEX" &&
                value.IntervalSeconds == settings.IntervalSeconds && value.OpenTimeUtc < bars[0].OpenTimeUtc)
            .OrderByDescending(value => value.OpenTimeUtc).Take(2500)
            .GroupBy(value => DateOnly.FromDateTime(value.OpenTimeUtc.ToOffset(IndiaOffset).DateTime))
            .OrderByDescending(value => value.Key).Take(5).SelectMany(value => value)
            .OrderBy(value => value.OpenTimeUtc).Select(ToBar).ToArray();
        if (!NiftyPreQuoteEntryGatePolicy.Evaluate(time, bars, previous, direction, structure,
                MultiTimeframeDecisionPolicy.Analyze(bars, direction, high, low),
                settings.IntervalSeconds, atr, high, low).Permitted) return new([], "PreQuoteGate");
        if (!OptionOpenInterestEntryPolicy.Evaluate(direction, bars[^1].Close, atr,
                frame.LatestOptionChain?.Strikes ?? [], frame.PreviousOptionChain?.Strikes ?? []).Permitted) return new([], "OptionOpenInterest");
        var priorSame = frame.ClosedTrades.Where(value => IsSide(value.Symbol, direction))
            .OrderByDescending(value => value.ExitAtUtc).FirstOrDefault();
        var priorExit = priorSame?.ExitAtUtc;
        if (priorSame is not null && !Specialty(priorSame.Strategy) &&
            !SensexFullStopReentryPolicy.AllowsEntry(now, priorExit,
                priorSame.ExitReason is "ObservedStop" or "PolicyStop" ? "StopLossHit" : priorSame.ExitReason,
                priorSame.NetPnl, priorSame.EntryPrice, priorSame.Quantity, settings.StopPercent)) return new([], "FullStopReentry");
        if (selection.PersistentDirectional &&
            !SensexMorningThesisReentryPolicy.AllowsEntry(time, true, priorExit)) return new([], "MorningThesisReentry");
        var persistent = selection.PersistentDirectional ||
            ProfessionalTradeFramework.Map(state) == ProfessionalTradeRegime.Directional;
        if (!StructuralReentryPolicy.Evaluate(bars, direction, priorExit, persistent).Permitted) return new([], "StructuralReentry");
        var opposite = frame.ClosedTrades.Where(value => !IsSide(value.Symbol, direction))
            .OrderByDescending(value => value.ExitAtUtc).Select(value => (DateTimeOffset?)value.ExitAtUtc)
            .FirstOrDefault();
        if (!DirectionFlipCooldownPolicy.Evaluate(opposite, now).Permitted) return new([], "DirectionFlipCooldown");
        var contract = Select(contracts, tradingDate, direction, bars[^1].Close,
            ProfessionalTradeFramework.Map(state) == ProfessionalTradeRegime.Directional ? 1 : 0);
        if (contract is null || contract.LotSize <= 0 || contract.TickSize <= 0m ||
            frame.OpenPositions.Any(value => value.Symbol == contract.TradingSymbol)) return new([], "ContractSelection");
        var dte = contract.ExpiryDate.DayNumber - tradingDate.DayNumber;
        if (!selection.Supplemental && !SensexAdaptiveSetupPolicy.EvaluateEvidenceGate(adaptive,
                time, dte, selection.Candidate.Confidence,
                rolledFromSameDayExpiry: IsSameDayExpiryRoll(contracts, tradingDate, direction))
                .Permitted) return new([], "EvidenceGate");
        var professional = new ProfessionalTradeAssessmentInput("sensex",
            selection.PersistentDirectional ? ProfessionalTradeRegime.Directional : ProfessionalTradeFramework.Map(state),
            selection.Candidate.Confidence, confirmation.RelativeVolume, adaptive.RoomToOpposingLevelAtr,
            null, null, null, dte, null, null, 0m, frame.PortfolioOpenPositions.Any(value =>
                value.Symbol.StartsWith("NIFTY", StringComparison.Ordinal) && IsSide(value.Symbol, direction)),
            0, selection.Supplemental || adaptive.Setup == "ContinuationOrRetest");
        return new([new(contract.TradingSymbol, now, selection.Candidate.Strategy,
            selection.Candidate.Confidence, checked(contract.LotSize * settings.Lots))
        {
            LotSize = contract.LotSize, UnderlyingDirection = direction, ContractExpiryDate = contract.ExpiryDate,
            StrategyDecisionAtUtc = now, RequiresDeferredQuoteAdmission = true, TriggerOnLastPrice = true,
            DeferredSensexProfessionalContext = professional,
            PlannedExitAtUtc = new DateTimeOffset(tradingDate.ToDateTime(settings.ForcedExit), IndiaOffset).ToUniversalTime()
        }], "Candidate");
    }

    public HistoricalReplayCandidate? AssessDeferredEntry(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayCandidate pending, HistoricalReplayQuote optionQuote)
    {
        if (!pending.RequiresDeferredQuoteAdmission || pending.DeferredSensexProfessionalContext is not { } context ||
            !optionQuote.IsFreshAsOf(frame.Market.AvailableAtUtc, TimeSpan.FromSeconds(settings.MaximumOptionAgeSeconds)) ||
            pending.Symbol != optionQuote.Symbol || optionQuote.AvailableAtUtc <= pending.DecidedAtUtc ||
            optionQuote.AvailableAtUtc - pending.DecidedAtUtc > TimeSpan.FromSeconds(settings.MaximumOptionAgeSeconds) ||
            optionQuote.BidPrice is not > 0m || optionQuote.OfferPrice is not > 0m ||
            optionQuote.BidPrice > optionQuote.OfferPrice || frame.OpenPositions.Any(value => value.Symbol == pending.Symbol))
            return null;
        var contract = contracts.SingleOrDefault(value => value.TradingSymbol == pending.Symbol);
        if (contract is null) return null;
        var quote = new GrowwQuote(optionQuote.LastPrice, optionQuote.AvailableAtUtc.ToUnixTimeMilliseconds(),
            optionQuote.BidPrice, null, optionQuote.OfferPrice, null, optionQuote.Volume, optionQuote.OpenInterest, null);
        var admission = SensexOptionAdmissionPolicy.Evaluate(new(quote, settings.StopPercent,
            contract.TickSize, contract.LotSize, context));
        return !admission.Permitted ? null : pending with
        {
            DecidedAtUtc = optionQuote.AvailableAtUtc, PlannedStopPrice = admission.Stop,
            PlannedTargetPrice = admission.Target, RequiresDeferredQuoteAdmission = false,
            DeferredSensexProfessionalContext = null
        };
    }

    private static SensexFutureConfirmation Confirmation(HistoricalReplayBar[] session,
        HistoricalReplayBar[] futures) => SensexFutureConfirmationPolicy.Evaluate(session.Select(ToBar).ToArray(),
        futures.Where(value => value.OpenTimeUtc <= session[^1].OpenTimeUtc).TakeLast(40)
            .Select(value => new SensexFutureVolumeBar(value.OpenTimeUtc, value.Volume)).ToArray());
    private static bool IsSide(string symbol, Direction direction) =>
        symbol.EndsWith(direction == Direction.Buy ? "CE" : "PE", StringComparison.Ordinal);
    private static bool Specialty(string strategy) => strategy.StartsWith("Hero Zero|", StringComparison.Ordinal) ||
        strategy.StartsWith("Expansion Pair|", StringComparison.Ordinal) || strategy.StartsWith("PaperOnly|Pyramid|", StringComparison.Ordinal);
    private static StrategyPriceBar ToBar(HistoricalReplayBar bar) => new(bar.OpenTimeUtc,
        bar.Open, bar.High, bar.Low, bar.Close);
    private Guid CandleId(DateTimeOffset at)
    {
        var bytes = underlyingInstrumentId.ToByteArray();
        BitConverter.GetBytes(at.UtcTicks).CopyTo(bytes, 0);
        bytes[15] = 1;
        return new Guid(bytes);
    }
    private static decimal Atr(HistoricalReplayBar[] bars) => bars.Skip(1).Select((bar, index) =>
        Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - bars[index].Close),
            Math.Abs(bar.Low - bars[index].Close)))).DefaultIfEmpty(0m).Average();

    internal static bool IsSameDayExpiryRoll(IReadOnlyList<NiftyOptionContractCandidate> contracts,
        DateOnly date, Direction direction) => contracts.Any(value =>
            value.Type == (direction == Direction.Buy ? InstrumentType.CallOption : InstrumentType.PutOption) &&
            value.TradingSymbol.StartsWith("SENSEX", StringComparison.Ordinal) && value.ExpiryDate == date);

    internal static NiftyOptionContractCandidate? Select(IReadOnlyList<NiftyOptionContractCandidate> contracts,
        DateOnly date, Direction direction, decimal price, int inTheMoneySteps)
    {
        var type = direction == Direction.Buy ? InstrumentType.CallOption : InstrumentType.PutOption;
        var eligible = contracts.Where(value => value.Type == type &&
            value.TradingSymbol.StartsWith("SENSEX", StringComparison.Ordinal)).ToArray();
        var start = IsSameDayExpiryRoll(contracts, date, direction) ? date.AddDays(1) : date;
        var strike = price + (direction == Direction.Buy ? -1 : 1) * inTheMoneySteps * 100m;
        return eligible.Where(value => value.ExpiryDate >= start && value.ExpiryDate <= start.AddDays(10))
            .OrderBy(value => value.ExpiryDate).ThenBy(value => Math.Abs(value.StrikePrice - strike))
            .ThenBy(value => value.TradingSymbol, StringComparer.Ordinal).FirstOrDefault();
    }
}
