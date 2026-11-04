using System.Text.Json;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// Research-only composition of the same pure premium-stop mathematics used
// by the live paper engine. The supplied plan must be selected from the
// correct as-of regime; reversal, partial exits and target extension are not
// reproduced here, so this adapter never attests full policy parity.
public sealed class HistoricalPremiumProtectionPolicy(
    IHistoricalReplayPortfolioPolicy entryPolicy,
    ProfessionalExitPlan exitPlan,
    string exchangeCode,
    bool breakEvenEnabled = true,
    bool trailingEnabled = true) : IHistoricalReplayPortfolioPolicy
{
    private readonly Dictionary<string, (DateTimeOffset EntryAtUtc,
        decimal HighestPrice)> favourableBySymbol = new(StringComparer.Ordinal);

    public string CodeVersion => entryPolicy.CodeVersion + "/premium-protection-v1";
    public bool ProductionPolicyParityVerified => false;
    public string ConfigurationManifest => entryPolicy.ConfigurationManifest +
        "|premiumProtection=" + JsonSerializer.Serialize(new
        {
            exitPlan, exchangeCode, breakEvenEnabled, trailingEnabled,
            CostSchedule = exchangeCode == "BSE"
                ? PaperTradingCostModel.BseScheduleVersion
                : PaperTradingCostModel.ScheduleVersion
        });

    public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
        HistoricalReplayPortfolioFrame frame) => entryPolicy.Evaluate(frame);

    public IReadOnlyList<HistoricalReplayStopUpdate> EvaluateStopUpdates(
        HistoricalReplayPortfolioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (exchangeCode is not ("NSE" or "BSE"))
            throw new InvalidOperationException(
                "The replay protection policy requires NSE or BSE costs.");
        if (entryPolicy.EvaluateStopUpdates(frame).Count != 0)
            throw new InvalidOperationException(
                "Compose one protective-stop policy at a time.");
        var updates = new List<HistoricalReplayStopUpdate>();
        foreach (var position in frame.OpenPositions)
        {
            if (position.InitialStopPrice <= 0m ||
                position.InitialStopPrice >= position.EntryPrice ||
                position.Quantity <= 0 ||
                !frame.Market.LatestQuotes.TryGetValue(position.Symbol,
                    out var quote) || quote.AvailableAtUtc < position.EntryAtUtc)
                continue;
            var observedSellMark = quote.BidPrice ?? quote.LastPrice;
            var highest = favourableBySymbol.TryGetValue(position.Symbol,
                    out var previous) && previous.EntryAtUtc == position.EntryAtUtc
                ? Math.Max(previous.HighestPrice, observedSellMark)
                : Math.Max(position.EntryPrice, observedSellMark);
            favourableBySymbol[position.Symbol] = (position.EntryAtUtc, highest);
            var originalQuantity = position.OriginalQuantity > 0 ?
                position.OriginalQuantity : position.Quantity;
            var proposed = PaperProtectiveStopPolicy.CalculateNetProtected(
                Direction.Buy, position.EntryPrice,
                position.InitialStopPrice, highest, exitPlan,
                breakEvenEnabled, trailingEnabled, originalQuantity,
                originalQuantity, exchangeCode);
            if (proposed > position.StopPrice)
                updates.Add(new(position.Symbol,
                    frame.Market.AvailableAtUtc, proposed));
        }
        return updates;
    }
}
