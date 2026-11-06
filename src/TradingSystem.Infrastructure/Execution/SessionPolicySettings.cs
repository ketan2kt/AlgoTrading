using Microsoft.Extensions.Options;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Risk;
using TradingSystem.Application.Strategies;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.Infrastructure.Execution;

public sealed class SessionPolicySettings(ITradingLotConfigurationService lots, IOptions<AutomatedPaperTradingOptions> automation,
    IOptions<MarketDataOptions> data, IOptions<PreliminaryRiskOptions> risk, IOptions<MarketRegimeOptions> regime,
    IOptions<PriceActionStrategyOptions> priceAction, IOptions<OpeningRangeBreakoutOptions> openingRange)
{
    public async Task<HistoricalNormalPolicyConfiguration> ReadAsync(CancellationToken cancellationToken)
    {
        var sizes = await lots.GetAsync(cancellationToken);
        var value = automation.Value;
        var feed = data.Value;
        var admission = new NiftyOptionAdmissionSettings(value.PermissivePaperExecution,
            value.MaximumOptionSpreadPercent, value.MaximumOptionPremium, value.MinimumOptionVolume,
            value.MinimumOptionOpenInterest, value.OptionStopLossPercent, value.OptionRewardToRiskRatio,
            value.MaximumConcurrentPositions, risk.Value.MaximumRiskPerTrade, risk.Value.MaximumCapitalExposure,
            value.MaximumDailyLoss, TimeOnly.ParseExact(value.EntryWindowStart, "HH:mm"),
            TimeOnly.ParseExact(value.EntryCutoff, "HH:mm"), TimeOnly.ParseExact(value.ForcedExit, "HH:mm"),
            feed.MaximumAgeSeconds, feed.MaximumAgeSeconds, value.SelectiveHedgingEnabled,
            value.HedgeMinimumAtrPercent, value.HedgeMinimumRelativeFuturesVolume, value.HedgeMinimumSignalConfidence,
            value.EnforceDailyLossLimit);
        var exits = new HistoricalNiftyExitSettings(admission.ForcedExitTime, value.BreakEvenEnabled,
            value.TrailingStopEnabled, value.PartialProfitEnabled, value.TargetExtensionEnabled,
            value.TargetExtensionMinimumConfidence, value.UnderlyingTrendInvalidationEnabled,
            value.MinimumReversalStructureStrength, value.RequiredReversalEvidenceCount,
            value.ProtectProfitableConfirmedReversals, value.ReversalProfitLockFraction,
            feed.MaximumAgeSeconds, feed.MaximumAgeSeconds, feed.CandleIntervalSeconds,
            value.DifferentiatedRunnerEnabled);
        var marketExits = new HistoricalMarketOptionExitSettings(new TimeOnly(15, 15),
            value.PartialProfitEnabled, value.TargetExtensionEnabled, value.UnderlyingTrendInvalidationEnabled,
            value.MinimumReversalStructureStrength, value.RequiredReversalEvidenceCount,
            value.ProtectProfitableConfirmedReversals, value.ReversalProfitLockFraction,
            feed.MaximumAgeSeconds, feed.MaximumAgeSeconds, feed.CandleIntervalSeconds,
            value.DifferentiatedRunnerEnabled);
        return new HistoricalNormalPolicyConfiguration(feed.CandleIntervalSeconds,
            value.MinimumSignalCandles, value.MinimumFuturesConfirmationCandles,
            TimeOnly.ParseExact(value.OpeningRangeEnd, "HH:mm"), sizes.NiftyLots, sizes.BankNiftyLots,
            value.OptionStopLossPercent, new(value.MaximumOptionDaysToExpiry,
                value.ExpiryDayInTheMoneySteps, value.NormalInTheMoneySteps, value.OptionStrikeStep),
            admission, exits, new(admission.EntryWindowStart, admission.EntryCutoff,
                new TimeOnly(15, 15), sizes.SensexLots, feed.CandleIntervalSeconds,
                value.OptionStopLossPercent, feed.MaximumAgeSeconds), marketExits,
            regime.Value, priceAction.Value, openingRange.Value)
            { WeeklyCarryForwardEnabled = value.WeeklyCarryForwardEnabled };
    }
}
