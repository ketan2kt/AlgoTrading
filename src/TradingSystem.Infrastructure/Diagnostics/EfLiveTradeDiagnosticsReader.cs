using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Diagnostics;
using TradingSystem.Infrastructure.Broker.Groww;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Diagnostics;

internal sealed class EfLiveTradeDiagnosticsReader(
    TradingDbContext db,
    GrowwBrokerGateway broker) : ILiveTradeDiagnosticsReader
{
    public async Task<LiveTradeDiagnosticsReport?> ReadAsync(Guid paperTradeId,
        CancellationToken cancellationToken)
    {
        if (paperTradeId == Guid.Empty) return null;
        var paper = await db.PaperTradeResults.AsNoTracking()
            .Where(value => value.Id == paperTradeId)
            .Select(value => new { value.SignalId, value.EntryPrice })
            .SingleOrDefaultAsync(cancellationToken);
        var position = paper is null ? await db.MarketPaperPositions.AsNoTracking()
            .Where(value => value.Id == paperTradeId)
            .Select(value => new { value.Market, value.Strategy, value.Status,
                value.EntryPrice, value.StopLoss, value.Target, value.OpenedAtUtc })
            .SingleOrDefaultAsync(cancellationToken) : null;
        if (paper is null && position is null) return null;
        var sourceId = paper?.SignalId ?? paperTradeId;
        var intent = await db.LiveExecutionIntents.AsNoTracking()
            .SingleOrDefaultAsync(value => value.SourceId == sourceId, cancellationToken);
        var market = (intent?.Market ?? position?.Market ?? "NIFTY").ToLowerInvariant();
        var compactSourceId = sourceId.ToString("N");
        var fromUtc = intent?.CreatedAtUtc ?? position?.OpenedAtUtc;
        var audits = await db.MarketStrategyAudits.AsNoTracking()
            .Where(value => value.Market == market &&
                (fromUtc == null || value.CandleTimeUtc >= fromUtc.Value.AddMinutes(-1)) &&
                (fromUtc == null || value.CandleTimeUtc <= fromUtc.Value.AddDays(1)) &&
                ((value.Outcome.StartsWith("LiveExecution:") &&
                  value.ReasonsJson.Contains(compactSourceId)) ||
                 value.Outcome == "LiveProtectionDesired:" + compactSourceId))
            .OrderBy(value => value.CandleTimeUtc)
            .Take(300)
            .Select(value => new LiveTradeTimelineEvent(value.CandleTimeUtc,
                value.Outcome, value.ReasonsJson))
            .ToListAsync(cancellationToken);

        var orders = new List<LiveBrokerOrderEvidence>(3);
        var limitations = new List<string>();
        if (intent is null)
        {
            limitations.Add("No live intent was persisted. A source-specific skip reason was not recorded, so switch timing, signal age, eligibility and worker state cannot be distinguished retrospectively.");
            if (position?.Strategy.StartsWith("PaperOnly|", StringComparison.Ordinal) == true)
                limitations.Add("This research-only source is excluded from live discovery by current policy. Before this policy was enforced, its label alone did not prove why an older order was absent.");
            return new LiveTradeDiagnosticsReport(paperTradeId, sourceId, market.ToUpperInvariant(),
                position?.Strategy, position?.Status ?? "Closed", "NoLiveIntent", null,
                null, null, position?.EntryPrice ?? paper!.EntryPrice,
                position?.StopLoss, position?.Target, null, audits, orders, limitations);
        }
        var references = new[]
        {
            (Role: "Entry", Reference: intent.ClientReference),
            (Role: "Protection", Reference: "LP" + intent.Id.ToString("N")[..16]),
            (Role: "FinalExit", Reference: "LX" + sourceId.ToString("N")[..16])
        };
        foreach (var item in references)
        {
            try
            {
                var order = await broker.GetOrderEvidenceAsync(item.Role, item.Reference,
                    cancellationToken);
                if (order is not null) orders.Add(order);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                limitations.Add($"{item.Role} broker lookup unavailable ({exception.GetType().Name}).");
            }
        }
        if (!orders.Any(value => value.Role is "Protection" or "FinalExit" &&
            value.Fills.Count > 0))
            limitations.Add("Broker exit fill was not returned; exit cause is not proven.");
        limitations.Add("Groww order detail and trade time fields do not explicitly identify the stop-trigger instant.");
        if (audits.Count == 0)
            limitations.Add("No historical live lifecycle events were persisted for this trade.");

        return new LiveTradeDiagnosticsReport(paperTradeId, sourceId, intent.Market,
            position?.Strategy, position?.Status ?? "Closed", intent.Status,
            intent.CreatedAtUtc, intent.SubmittedAtUtc, intent.ProtectedAtUtc,
            position?.EntryPrice ?? paper!.EntryPrice,
            position?.StopLoss, position?.Target, intent.AverageFillPrice,
            audits, orders, limitations);
    }
}
