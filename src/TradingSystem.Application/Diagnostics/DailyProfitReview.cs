using System.Text.Json;

namespace TradingSystem.Application.Diagnostics;

public sealed record DailyEntryEvidence(string Status, DateTimeOffset? RecordedAtUtc,
    decimal? ExtensionAtr, decimal? RoomAtr, decimal? CandleAgeSeconds, IReadOnlyList<string> Reasons);
public sealed record DailyExitObservation(int HorizonMinutes, DateTimeOffset ObservedAtUtc,
    decimal Price, decimal GrossDifference);
public sealed record DailyTradeReview(Guid TradeId, DailyEntryEvidence Entry,
    decimal? GrossGiveback, decimal? RetainedPercent, string ProfitEvidence,
    string ExitAssessment, int? FinalExitUnits, IReadOnlyList<DailyExitObservation> FollowUps);
public sealed record DailyMarketReview(string Market, int ClosedTrades, int OpenTrades,
    decimal NetPnl, decimal Costs, int ComparablePeakTrades, decimal? ObservedPeakSubtotal,
    decimal? GrossGivebackSubtotal, int? PositivePeakToNetLossTrades);
public sealed record DailyProfitReview(IReadOnlyList<DailyMarketReview> Markets,
    IReadOnlyList<DailyTradeReview> Trades);
public sealed record DailyEntryReceipt(DateTimeOffset At, string Json);
public sealed record DailyFollowUpPrice(int HorizonMinutes, DateTimeOffset At, decimal Price);

// Read-only attribution from saved receipts. Never re-evaluate entries using future bars,
// turn sampled follow-ups into fills, or subtract fees from a gross peak twice.
public static class DailyProfitReviewBuilder
{
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    private static readonly string[] ReasonFields = ["supportingReasons", "reasons", "reasoning"];
    private static readonly string[] TimingFlags = ["staleCandleHypothesis", "extendedHypothesis", "limitedRoomHypothesis"];
    public static DailyProfitReview Build(IReadOnlyList<DailyTradingTrade> trades,
        IReadOnlyDictionary<Guid, DailyEntryReceipt> entries,
        IReadOnlyDictionary<Guid, IReadOnlyList<DailyFollowUpPrice>> followUps,
        IReadOnlyDictionary<Guid, int> finalUnits,
        IReadOnlySet<Guid> incomparablePeaks)
    {
        var rows = trades.Select(t => {
            var comparable = t.ClosedAtUtc.HasValue && t.MaximumFavourableExcursion is >= 0 &&
                t.ExcursionSampleCount > 2 && !incomparablePeaks.Contains(t.TradeId) &&
                !t.ExcursionCoverage.Contains("legacy excursion accounting", StringComparison.OrdinalIgnoreCase) &&
                !t.ExcursionCoverage.Contains("unavailable", StringComparison.OrdinalIgnoreCase) &&
                t.MaximumFavourableExcursion >= Math.Max(0, t.GrossPnl);
            var units = finalUnits.TryGetValue(t.TradeId, out var quantity) && quantity > 0 ? (int?)quantity : null;
            var direction = t.Source == "PaperTradeResult" || t.Direction == "Buy" ? 1m : -1m;
            var observations = !t.ClosedAtUtc.HasValue || units is null ? [] :
                followUps.GetValueOrDefault(t.TradeId, [])
                    .Where(p => p.Price > 0 && p.HorizonMinutes is 5 or 15 or 30 or 60 &&
                        p.At >= t.ClosedAtUtc.Value.AddMinutes(p.HorizonMinutes) &&
                        p.At <= t.ClosedAtUtc.Value.AddMinutes(p.HorizonMinutes + 10) &&
                        DateOnly.FromDateTime(p.At.ToOffset(TimeSpan.FromHours(5.5)).DateTime) ==
                        DateOnly.FromDateTime(t.ClosedAtUtc.Value.ToOffset(TimeSpan.FromHours(5.5)).DateTime) &&
                        TimeOnly.FromDateTime(p.At.ToOffset(TimeSpan.FromHours(5.5)).DateTime) <= new TimeOnly(15, 30))
                    .GroupBy(p => p.HorizonMinutes).Select(g => g.MinBy(p => p.At)!)
                    .OrderBy(p => p.HorizonMinutes)
                    .Select(p => new DailyExitObservation(p.HorizonMinutes, p.At, p.Price,
                        (p.Price - t.ExitOrCurrentPrice) * units.Value * direction)).ToArray();
            var rises = observations.Any(p => p.GrossDifference > 0);
            var falls = observations.Any(p => p.GrossDifference < 0);
            var assessment = observations.Length == 0 ? "No usable follow-up" : rises && falls
                ? "Mixed follow-up" : rises ? "Price recovered after exit" : falls
                ? "Price fell further after exit" : "Unchanged at sampled times";
            return new DailyTradeReview(t.TradeId, Entry(entries.GetValueOrDefault(t.TradeId)),
                comparable ? Math.Max(0, t.MaximumFavourableExcursion!.Value - t.GrossPnl) : null,
                comparable && t.MaximumFavourableExcursion > 0 ?
                    100 * t.GrossPnl / t.MaximumFavourableExcursion.Value : null,
                comparable ? "Observed gross peak; sampled, not an achievable exit promise."
                    : !t.ClosedAtUtc.HasValue ? "Open trade; final retention pending."
                    : "Not comparable: missing/legacy peak basis, incomplete path or changed position size.",
                assessment, units, observations);
        }).ToArray();
        var byId = rows.ToDictionary(r => r.TradeId);
        var markets = Markets.Select(m => {
            var selected = trades.Where(t => t.Market.Replace("-", "").Equals(m, StringComparison.OrdinalIgnoreCase)).ToArray();
            var closed = selected.Where(t => t.ClosedAtUtc.HasValue).ToArray();
            var comparable = closed.Where(t => byId[t.TradeId].GrossGiveback.HasValue).ToArray();
            return new DailyMarketReview(m, closed.Length, selected.Length - closed.Length,
                closed.Sum(t => t.NetRealisedPnl), closed.Sum(t => t.Costs), comparable.Length,
                comparable.Length == 0 ? null : comparable.Sum(t => t.MaximumFavourableExcursion!.Value),
                comparable.Length == 0 ? null : comparable.Sum(t => byId[t.TradeId].GrossGiveback!.Value),
                comparable.Length == 0 ? null : comparable.Count(t => t.MaximumFavourableExcursion > 0 && t.NetRealisedPnl < 0));
        }).ToArray();
        return new(markets, rows);
    }

    public static DailyEntryEvidence Entry(DailyEntryReceipt? receipt)
    {
        var unavailable = new DailyEntryEvidence("Timing unverified", null, null, null, null,
            ["No linked entry-quality receipt. A losing trade alone does not prove a late entry."]);
        if (receipt is null) return unavailable;
        try
        {
            using var doc = JsonDocument.Parse(receipt.Json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unavailable;
            var timing = Property(root, "timing");
            var location = Property(root, "location");
            var context = Property(location, "context");
            var reasons = new List<string>();
            foreach (var field in ReasonFields)
                AddReasons(Property(root, field), reasons);
            AddReasons(Property(Property(root, "entryQuality"), "reasons"), reasons);
            AddReasons(location, reasons);
            AddReasons(context, reasons);
            AddReasons(Property(location, "reasons"), reasons);
            AddReasons(Property(context, "reasons"), reasons);
            var extension = Number(timing, "extensionAtr");
            var room = Number(timing, "roomAtr");
            var age = Number(timing, "candleCloseAgeSeconds");
            var flagged = TimingFlags
                .Any(name => Property(timing, name).ValueKind == JsonValueKind.True);
            var fillTime = Property(root, "paperEntryRecordedAtUtc");
            var recordedAt = fillTime.ValueKind == JsonValueKind.String && fillTime.TryGetDateTimeOffset(out var fillAt)
                ? fillAt : receipt.At;
            return new(flagged ? "Timing flag (advisory)" : "Entry evidence recorded; timing unverified",
                recordedAt, extension, room, age, reasons.Distinct().Take(8)
                    .Append("First actionable setup time is not established by this receipt; do not infer entry lateness from candle age.").ToArray());
        }
        catch (JsonException) { return unavailable; }
    }

    public static Guid? PositionId(string json)
    {
        try { using var doc = JsonDocument.Parse(json); var id = Property(doc.RootElement, "positionId");
            return id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var value) ? value : null; }
        catch (JsonException) { return null; }
    }

    public static DailyFollowUpPrice? FollowUp(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            var horizon = Number(root, "horizonMinutes"); var price = Number(root, "observedPrice");
            var at = Property(root, "observedAtUtc");
            return horizon is >= 1 and <= 60 && horizon == decimal.Truncate(horizon.Value) && price is > 0 &&
                at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var observed)
                ? new((int)horizon.Value, observed, price.Value) : null;
        }
        catch (JsonException) { return null; }
    }

    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        ? root.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
    private static decimal? Number(JsonElement root, string name) => Property(root, name) is var p &&
        p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var value) ? value : null;
    private static void AddReasons(JsonElement value, List<string> reasons)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            reasons.Add(text.Length <= 600 ? text : text[..600]);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray().Take(12)) AddReasons(item, reasons);
    }
}
