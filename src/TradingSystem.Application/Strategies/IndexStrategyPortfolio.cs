namespace TradingSystem.Application.Strategies;

// One composition for production and a frozen replay module. No service
// provider, persistence, broker execution or clock is available here.
public static class IndexStrategyPortfolio
{
    public static ITradingStrategy Create(PriceActionStrategyOptions priceAction,
        OpeningRangeBreakoutOptions openingRange) => new CompositeTradingStrategy([
        new OpeningRangeBreakoutStrategy(openingRange),
        new OpeningRangeRetestStrategy(priceAction),
        new VwapTrendPullbackStrategy(priceAction),
        new EmaPullbackContinuationStrategy(priceAction),
        new RangeBreakoutRetestStrategy(priceAction),
        new VwapRejectionReversalStrategy(priceAction),
        new MomentumExpansionStrategy(priceAction)
    ], new NiftyBreakoutRetestStrategy(priceAction));
}
