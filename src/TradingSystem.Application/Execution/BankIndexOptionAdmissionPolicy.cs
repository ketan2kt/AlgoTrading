using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// The banking-index paper engine and historical replay share this option
// quote-to-protection rule. As-of quote availability is checked by the caller.
public sealed record BankIndexOptionAdmissionDecision(
    bool Permitted, bool SpreadRejected, decimal Entry,
    decimal Stop, decimal Target);

public static class BankIndexOptionAdmissionPolicy
{
    public static BankIndexOptionAdmissionDecision Evaluate(
        decimal lastPrice, decimal? bidPrice, decimal? offerPrice,
        int lotSize, decimal tickSize, decimal stopLossPercent)
    {
        var entry = offerPrice ?? lastPrice;
        if (entry <= 0m || lotSize <= 0 || tickSize <= 0m ||
            stopLossPercent <= 0m)
            return new(false, false, entry, 0m, 0m);
        if (bidPrice is > 0m && offerPrice is > 0m &&
            (offerPrice.Value - bidPrice.Value) / offerPrice.Value > .015m)
            return new(false, true, entry, 0m, 0m);

        var distance = entry * stopLossPercent / 100m;
        var stop = RoundToTick(entry - distance, tickSize);
        var target = RoundToTick(entry + distance, tickSize);
        return new(PaperPriceGeometryPolicy.IsValid(Direction.Buy,
            entry, stop, target), false, entry, stop, target);
    }

    private static decimal RoundToTick(decimal value, decimal tick) =>
        Math.Max(tick, Math.Round(value / tick, MidpointRounding.AwayFromZero) * tick);
}
