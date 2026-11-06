using TradingSystem.Application.Regime;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Risk;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// This reuses the production's pure regime/strategy/filter components, but is
// deliberately named signal discovery: quote-spread, complete OI history,
// portfolio risk and broker-execution gates are not yet reproducible from the
// old archive. Received-at option-chain OI is used only where present.
public sealed class HistoricalNiftySignalPolicy(
    Guid underlyingInstrumentId,
    string spotSymbol,
    string futuresSymbol,
    DateOnly tradingDate,
    decimal previousClose,
    TimeOnly openingRangeEnd,
    int minimumSpotCandles,
    int minimumFuturesCandles,
    int candleIntervalSeconds,
    int lots,
    IReadOnlyCollection<NiftyOptionContractCandidate> optionContracts,
    NiftyOptionSelectionOptions optionSelection,
    MarketRegimeEngine regimeEngine,
    ITradingStrategy strategy,
    NiftyOptionAdmissionSettings? admissionSettings = null) : IHistoricalReplayPortfolioPolicy
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
    // Entry decisions use the production-shared feature, timing, structural,
    // quote and risk policies. The discovery-only constructor is not certified.
    public bool ProductionPolicyParityVerified => admissionSettings is not null;

    public string CodeVersion => $"nifty-signal-adapter-v11:{strategy.StrategyId}:{strategy.Version}/{OptionRiskBudgetPolicy.Version}";
    public string ConfigurationManifest => System.Text.Json.JsonSerializer.Serialize(new
    {
        spotSymbol, futuresSymbol, tradingDate, previousClose,
        openingRangeEnd, minimumSpotCandles, minimumFuturesCandles,
        candleIntervalSeconds, lots, optionSelection, admissionSettings,
        Regime = regimeEngine.ConfigurationManifest,
        StrategyConfiguration = strategy.ConfigurationManifest,
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
        if (DateOnly.FromDateTime(asOf.ToOffset(IndiaOffset).DateTime) != tradingDate ||
            previousClose <= 0m || lots <= 0 || candleIntervalSeconds <= 0)
            return [];
        var indiaTime = TimeOnly.FromDateTime(asOf.ToOffset(IndiaOffset).DateTime);
        if (admissionSettings is not null &&
            !IndexEntryWindowPolicy.AllowsEntry(indiaTime,
                admissionSettings.EntryWindowStart,
                admissionSettings.EntryCutoff)) return [];
        if (admissionSettings is not null &&
            (!frame.Market.LatestQuotes.TryGetValue(spotSymbol,
                 out var underlyingObservation) ||
             underlyingObservation.LastPrice <= 0m ||
             !underlyingObservation.IsFreshAsOf(asOf, TimeSpan.FromSeconds(
                 admissionSettings.MaximumUnderlyingQuoteAgeSeconds)))) return [];
        var spot = CurrentSession(frame.Market.CompletedBars, spotSymbol);
        var futures = CurrentSession(frame.Market.CompletedBars, futuresSymbol);
        if (spot.Length < Math.Max(2, minimumSpotCandles) ||
            futures.Length < Math.Max(2, minimumFuturesCandles)) return [];
        var latest = spot[^1];
        if (latest.AvailableAtUtc != asOf) return [];
        var alignedFuture = futures.Where(value => value.OpenTimeUtc <= latest.OpenTimeUtc)
            .ToArray();
        // The production paper path requires two aligned futures candles but
        // does not itself demand an exact-time future candle or a contiguous
        // tail. Keep the decision path identical; archive coverage excludes
        // incomplete sessions before comparison instead of inventing a
        // stricter replay-only trading rule.
        if (alignedFuture.Length < 2) return [];
        var features = NiftySignalFeaturePolicy.Calculate(
            spot.Select(ToStrategyBar).ToArray(),
            alignedFuture.Select(value => new StrategyPriceBar(
                value.OpenTimeUtc, value.Open, value.High, value.Low,
                value.Close, value.Volume)).ToArray(), openingRangeEnd);
        if (features is null) return [];
        var relativeVolume = features.RelativeFuturesVolume;
        var vwap = features.Vwap;
        var fast = features.FastEma;
        var slow = features.SlowEma;
        var atr = features.AtrPercent;
        if (atr <= 0m) return [];
        var openingHigh = features.OpeningRangeHigh;
        var openingLow = features.OpeningRangeLow;
        var sessionId = SessionId(tradingDate);
        var regime = regimeEngine.Evaluate(new MarketRegimeInput(sessionId,
            latest.OpenTimeUtc, latest.Close, previousClose, spot[0].Open,
            openingHigh, openingLow, vwap, fast, slow, atr,
            relativeVolume, 1m, true));
        var recent = spot.TakeLast(30).Select(ToStrategyBar).ToArray();
        var lastCompletedSignal = frame.ClosedTrades
            .OrderByDescending(value => value.DecisionAtUtc)
            .Select(value => (DateTimeOffset?)value.DecisionAtUtc).FirstOrDefault();
        // Production counts every opened position, including still-open ones.
        var tradesByStrategy = frame.ClosedTrades
            .Select(value => value.Strategy)
            .Concat(frame.OpenPositions.Select(value => value.Strategy))
            .GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(value => value.Key, value => value.Count(),
                StringComparer.Ordinal);
        var context = new StrategyEvaluationContext(underlyingInstrumentId,
            asOf, latest.Close, openingHigh, openingLow, relativeVolume,
            regime.Regime, regime.DirectionalBias, regime.Confidence,
            regime.TradingPermitted, true, lastCompletedSignal,
            frame.ClosedTrades.Count + frame.OpenPositions.Count)
        {
            Vwap = vwap,
            FastEma = fast,
            SlowEma = slow,
            AtrPercent = atr,
            RecentCandles = recent,
            TradesByStrategyToday = tradesByStrategy,
            MarketStructure = MarketStructureAnalyzer.Analyze(recent)
        };
        var signal = strategy.EvaluateDetailed(context).Signal;
        if (signal is null || !FirstEntryTimingPolicy.Evaluate(recent,
                signal.Direction).Permitted) return [];
        var marketState = MarketStateRouterV2.Analyze(recent, vwap, fast, slow);
        var persistentDirection = ProfessionalTradeFramework.Map(marketState) ==
            ProfessionalTradeRegime.Directional;
        var priorSameDirectionExit = frame.ClosedTrades
            .Where(value => signal.Direction == Direction.Buy
                ? value.Symbol.EndsWith("CE", StringComparison.Ordinal)
                : value.Symbol.EndsWith("PE", StringComparison.Ordinal))
            .OrderByDescending(value => value.ExitAtUtc)
            .Select(value => (DateTimeOffset?)value.ExitAtUtc).FirstOrDefault();
        if (!StructuralReentryPolicy.Evaluate(recent, signal.Direction,
                priorSameDirectionExit, persistentDirection).Permitted) return [];
        var structure = NiftySignalFeaturePolicy.EntryStructure(context);
        var sessionBars = spot.Select(ToStrategyBar).ToArray();
        var previousSessions = frame.Market.CompletedBars
            .Where(value => value.Symbol == spotSymbol &&
                            value.IntervalSeconds == candleIntervalSeconds &&
                            value.OpenTimeUtc < spot[0].OpenTimeUtc)
            .GroupBy(value => DateOnly.FromDateTime(
                value.OpenTimeUtc.ToOffset(IndiaOffset).DateTime))
            .OrderByDescending(value => value.Key).Take(5)
            .OrderBy(value => value.Key).SelectMany(value => value)
            .OrderBy(value => value.OpenTimeUtc);
        var previousBars = previousSessions.Select(ToStrategyBar).ToArray();
        var multiTimeframe = MultiTimeframeDecisionPolicy.Analyze(sessionBars,
            signal.Direction, openingHigh, openingLow);
        if (!NiftyPreQuoteEntryGatePolicy.Evaluate(
                TimeOnly.FromDateTime(asOf.ToOffset(IndiaOffset).DateTime),
                sessionBars, previousBars, signal.Direction, structure,
                multiTimeframe, candleIntervalSeconds,
                context.CurrentPrice * atr / 100m,
                openingHigh, openingLow).Permitted) return [];
        var underlyingRiskDistance = Math.Abs(signal.ProposedEntry -
            signal.ProposedStopLoss);
        if (underlyingRiskDistance <= 0m)
            underlyingRiskDistance = context.CurrentPrice * atr / 100m;
        var currentOi = frame.LatestOptionChain?.Strikes ?? [];
        var priorOi = frame.PreviousOptionChain?.Strikes ?? [];
        if (!OptionOpenInterestEntryPolicy.Evaluate(signal.Direction,
                context.CurrentPrice, underlyingRiskDistance, currentOi,
                priorOi).Permitted) return [];
        var strongRegimeAligned = regime.DirectionalBias == signal.Direction &&
            regime.Regime is MarketRegime.GapDownContinuation or
                MarketRegime.GapUpContinuation or MarketRegime.StrongBearishTrend or
                MarketRegime.StrongBullishTrend;
        if (!PaperEntryQualityPolicy.Evaluate(structure, signal.Direction,
                signal.StrategyId, strongRegimeAligned, regime.Confidence,
                relativeVolume).Permitted) return [];

        var strategyId = signal.StrategyId;
        if (admissionSettings is not null)
        {
            // The production adaptive policy is currently called with
            // unbounded counts and zero cooldown. Preserve its other branch:
            // opposing exposure requires the selective-hedge confirmation.
            var hasOpposingExposure = frame.OpenPositions.Any(value =>
                value.UnderlyingDirection is { } direction &&
                direction != signal.Direction);
            if (hasOpposingExposure)
            {
                if (!admissionSettings.SelectiveHedgingEnabled) return [];
                var hedge = SelectiveHedgePolicy.Evaluate(new(
                    true, frame.OpenPositions.Any(value =>
                        value.Strategy.StartsWith("selective-hedge-",
                            StringComparison.Ordinal)),
                    atr, relativeVolume, signal.Confidence, regime.Regime),
                    admissionSettings.HedgeMinimumAtrPercent,
                    admissionSettings.HedgeMinimumRelativeFuturesVolume,
                    admissionSettings.HedgeMinimumSignalConfidence);
                if (!hedge.Approved) return [];
                strategyId = $"selective-hedge-{strategyId}";
            }
        }

        var selection = optionSelection with
        {
            NormalInTheMoneySteps = persistentDirection
                ? Math.Max(1, optionSelection.NormalInTheMoneySteps)
                : optionSelection.NormalInTheMoneySteps
        };
        frame.Market.LatestQuotes.TryGetValue(spotSymbol, out var spotQuote);
        var contract = NiftyOptionContractSelector.Select(optionContracts,
            signal.Direction,
            admissionSettings is null ? latest.Close : spotQuote!.LastPrice,
            tradingDate, selection);
        if (contract is null || admissionSettings is null &&
            !HistoricalOptionEligibility.HasFreshAsOfPrice(frame.Market,
                contract.TradingSymbol, TimeSpan.FromSeconds(90)))
            return [];
        if (frame.OpenPositions.Any(value =>
                value.Symbol == contract.TradingSymbol)) return [];
        if (admissionSettings is not null)
        {
            var sameDirectionLosses = frame.ClosedTrades
                .OrderByDescending(value => value.ExitAtUtc)
                .TakeWhile(value => value.NetPnl < 0m).Count();
            // Production's Nifty risk state is rebuilt from Nifty signals;
            // other markets are consulted only for the explicit correlated
            // Sensex exposure check below.
            var existingCapital = frame.OpenPositions.Sum(value =>
                value.EntryPrice * value.Quantity);
            var openPnl = frame.OpenPositions.Sum(value =>
                (value.LatestPrice - value.EntryPrice) * value.Quantity);
            var admissionInput = new NiftyOptionAdmissionInput(
                new GrowwQuote(0m, null, null, null, null, null,
                    null, null, null),
                admissionSettings.PermissivePaperExecution,
                admissionSettings.MaximumSpreadPercent,
                admissionSettings.MaximumPremium,
                admissionSettings.MinimumVolume,
                admissionSettings.MinimumOpenInterest,
                tradingDate, contract.ExpiryDate,
                admissionSettings.StopLossPercent,
                admissionSettings.RewardToRiskRatio, contract.TickSize,
                contract.LotSize, lots,
                new ProfessionalTradeAssessmentInput("nifty",
                    ProfessionalTradeFramework.Map(marketState), signal.Confidence,
                    relativeVolume, structure.RoomToRiskRatio,
                    null, null, null, 0, null, null, 0m,
                    frame.PortfolioOpenPositions.Any(value =>
                        value.Symbol.StartsWith("SENSEX", StringComparison.Ordinal) &&
                        value.Symbol.EndsWith(signal.Direction == Direction.Buy
                            ? "CE" : "PE", StringComparison.Ordinal)),
                    sameDirectionLosses,
                    StructuralReentryPolicy.Evaluate(recent, signal.Direction,
                        priorSameDirectionExit, persistentDirection).NewStructureObserved),
                new PaperPortfolioRiskInput(0m, 0m, contract.LotSize, lots,
                    admissionSettings.MaximumRiskPerTrade,
                    admissionSettings.MaximumCapitalExposure, existingCapital,
                    frame.ClosedTrades.Sum(value => value.NetPnl), openPnl,
                    admissionSettings.MaximumDailyLoss,
                    frame.OpenPositions.Count,
                    admissionSettings.MaximumConcurrentPositions,
                    frame.Controls.NiftyKillSwitch, !frame.Controls.NiftyReconciliationHold, true,
                    frame.Controls.NiftyDailyLossOverride));
            return [new HistoricalReplayCandidate(contract.TradingSymbol,
                asOf, strategyId, signal.Confidence,
                checked(contract.LotSize * lots))
            {
                LotSize = contract.LotSize,
                UnderlyingDirection = signal.Direction,
                SignalRegime = signal.Regime,
                ContractExpiryDate = contract.ExpiryDate,
                PlannedExitAtUtc = new DateTimeOffset(
                    tradingDate.ToDateTime(admissionSettings.ForcedExitTime),
                    IndiaOffset).ToUniversalTime(),
                DeferredNiftyAdmission = admissionInput,
                MaximumAdmissionQuoteDelaySeconds = admissionSettings.MaximumOptionQuoteAgeSeconds,
                StrategyDecisionAtUtc = asOf
            }];
        }
        return [new HistoricalReplayCandidate(contract.TradingSymbol, asOf,
            strategyId, signal.Confidence,
            checked(contract.LotSize * lots))
        {
            LotSize = contract.LotSize,
            UnderlyingDirection = signal.Direction,
            SignalRegime = signal.Regime,
            ContractExpiryDate = contract.ExpiryDate
        }];
    }

    public HistoricalReplayCandidate? AssessDeferredEntry(
        HistoricalReplayPortfolioFrame frame,
        HistoricalReplayCandidate pending,
        HistoricalReplayQuote optionQuote)
    {
        if (admissionSettings is null ||
            !optionQuote.IsFreshAsOf(frame.Market.AvailableAtUtc, TimeSpan.FromSeconds(admissionSettings.MaximumOptionQuoteAgeSeconds)) ||
            pending.DeferredNiftyAdmission is not { } frozen ||
            pending.Symbol != optionQuote.Symbol ||
            optionQuote.AvailableAtUtc <= pending.DecidedAtUtc ||
            optionQuote.AvailableAtUtc - pending.DecidedAtUtc >
                TimeSpan.FromSeconds(admissionSettings.MaximumOptionQuoteAgeSeconds))
            return null;
        var quote = new GrowwQuote(optionQuote.LastPrice,
            optionQuote.AvailableAtUtc.ToUnixTimeMilliseconds(),
            optionQuote.BidPrice, null, optionQuote.OfferPrice, null,
            optionQuote.Volume, optionQuote.OpenInterest, null);
        var open = frame.OpenPositions;
        var risk = frozen.PortfolioContext with
        {
            ExistingCapitalExposure = open.Sum(value =>
                value.EntryPrice * value.Quantity),
            DailyRealisedPnl = frame.ClosedTrades.Sum(value =>
                value.NetPnl),
            DailyUnrealisedPnl = open.Sum(value =>
                (value.LatestPrice - value.EntryPrice) * value.Quantity),
            OpenPositions = open.Count,
            KillSwitchActive = frame.Controls.NiftyKillSwitch,
            BrokerReconciled = !frame.Controls.NiftyReconciliationHold,
            DailyLossLimitOverridden = frame.Controls.NiftyDailyLossOverride
        };
        var professional = frozen.ProfessionalContext with
        {
            CorrelatedSameDirectionExposure = frame.PortfolioOpenPositions.Any(value =>
                value.Symbol.StartsWith("SENSEX", StringComparison.Ordinal) &&
                value.Symbol.EndsWith(pending.UnderlyingDirection == Direction.Buy
                    ? "CE" : "PE", StringComparison.Ordinal)),
            ConsecutiveSameDirectionLosses = frame.ClosedTrades
                .OrderByDescending(value => value.ExitAtUtc)
                .TakeWhile(value => value.NetPnl < 0m).Count()
        };
        var decision = NiftyOptionAdmissionPolicy.Evaluate(frozen with
        {
            Quote = quote,
            PortfolioContext = risk,
            ProfessionalContext = professional
        });
        return !decision.Approved ? null : pending with
        {
            DecidedAtUtc = optionQuote.AvailableAtUtc,
            Quantity = decision.RiskDecision!.ApprovedQuantity,
            PlannedStopPrice = decision.StopLoss,
            PlannedTargetPrice = decision.Target,
            DeferredNiftyAdmission = null
        };
    }

    private HistoricalReplayBar[] CurrentSession(
        IReadOnlyList<HistoricalReplayBar> bars, string symbol) => bars
        .Where(value => value.Symbol == symbol &&
            value.IntervalSeconds == candleIntervalSeconds &&
            DateOnly.FromDateTime(value.OpenTimeUtc.ToOffset(IndiaOffset).DateTime) ==
                tradingDate)
        .OrderBy(value => value.OpenTimeUtc).ToArray();

    // The current live Nifty decision context passes price bars without spot
    // volume. Keep this adapter identical until both paths change together.
    private static StrategyPriceBar ToStrategyBar(HistoricalReplayBar value) =>
        new(value.OpenTimeUtc, value.Open, value.High, value.Low, value.Close);

    private static Guid SessionId(DateOnly date)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(date.DayNumber).CopyTo(bytes, 0);
        bytes[15] = 7;
        return new Guid(bytes);
    }
}
