using TradingSystem.Application.Broker;

namespace TradingSystem.Application.Execution;

public sealed record OptionQuoteValidationResult(
    bool Approved,
    decimal EntryPrice,
    decimal ExitReferencePrice,
    decimal SpreadPercent,
    IReadOnlyList<string> RejectionReasons);

public static class OptionPaperTradePricing
{
    public static OptionQuoteValidationResult ForPermissiveSimulation(GrowwQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        var entryPrice = quote.OfferPrice is > 0 ? quote.OfferPrice.Value
            : quote.LastPrice > 0 ? quote.LastPrice
            : quote.BidPrice is > 0 ? quote.BidPrice.Value
            : 0m;
        var exitPrice = quote.BidPrice is > 0 ? quote.BidPrice.Value : entryPrice;
        var reasons = entryPrice > 0
            ? Array.Empty<string>()
            : ["A positive option premium is required for paper execution."];

        return new(reasons.Length == 0, entryPrice, exitPrice, 0m, reasons);
    }

    public static OptionQuoteValidationResult Validate(
        GrowwQuote quote,
        decimal maximumSpreadPercent,
        decimal maximumPremium,
        decimal minimumVolume,
        decimal minimumOpenInterest)
    {
        ArgumentNullException.ThrowIfNull(quote);
        var reasons = new List<string>();
        if (quote.BidPrice is not > 0 || quote.OfferPrice is not > 0)
            reasons.Add("A positive bid and offer are required.");
        if (quote.BidPrice is > 0 && quote.OfferPrice is > 0 && quote.OfferPrice < quote.BidPrice)
            reasons.Add("The option quote has an inverted spread.");
        if (quote.OfferPrice is > 0 && quote.OfferPrice > maximumPremium)
            reasons.Add("The option premium exceeds the configured maximum.");
        if (quote.Volume is null || quote.Volume < minimumVolume)
            reasons.Add("Option volume is below the configured minimum.");
        if (quote.OpenInterest is null || quote.OpenInterest < minimumOpenInterest)
            reasons.Add("Option open interest is below the configured minimum.");

        var spread = 0m;
        if (quote.BidPrice is > 0 && quote.OfferPrice is > 0 && quote.OfferPrice >= quote.BidPrice)
        {
            var midpoint = (quote.BidPrice.Value + quote.OfferPrice.Value) / 2m;
            spread = midpoint > 0 ? (quote.OfferPrice.Value - quote.BidPrice.Value) / midpoint * 100m : 0m;
            if (spread > maximumSpreadPercent)
                reasons.Add("The option bid/ask spread exceeds the configured maximum.");
        }

        return new(reasons.Count == 0,
            reasons.Count == 0 ? quote.OfferPrice!.Value : 0m,
            reasons.Count == 0 ? quote.BidPrice!.Value : 0m,
            spread,
            reasons);
    }

    public static (decimal StopLoss, decimal Target) ProtectivePrices(
        decimal entryPrice,
        decimal stopLossPercent,
        decimal rewardToRiskRatio,
        decimal tickSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryPrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stopLossPercent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rewardToRiskRatio);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickSize);
        var risk = entryPrice * stopLossPercent / 100m;
        var stop = Math.Floor((entryPrice - risk) / tickSize) * tickSize;
        var target = Math.Ceiling((entryPrice + risk * rewardToRiskRatio) / tickSize) * tickSize;
        return (Math.Max(tickSize, stop), target);
    }
}
