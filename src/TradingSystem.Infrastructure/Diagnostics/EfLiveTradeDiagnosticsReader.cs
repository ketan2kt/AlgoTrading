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
        var fillAt = position?.OpenedAtUtc ?? await db.PaperBrokerEvents.AsNoTracking()
            .Where(x => x.ClientReference == compactSourceId + "-ENTRY" && x.EventType == "OrderFilled")
            .MinAsync(x => (DateTimeOffset?)x.OccurredAtUtc,cancellationToken);
        var fromUtc = fillAt ?? intent?.CreatedAtUtc;
        var sourceFilter = System.Text.Json.JsonSerializer.Serialize(new { sourceIdCompact = compactSourceId });
        var positionFilter = System.Text.Json.JsonSerializer.Serialize(new { positionId = sourceId });
        var query = db.MarketStrategyAudits.AsNoTracking().Where(value => value.Market == market);
        if (fromUtc is { } since)
        {
            var lower = since.AddMinutes(-15); // Opening audit is indexed by the decision candle.
            var upper = since.AddDays(1);
            query = query.Where(value => value.CandleTimeUtc >= lower && value.CandleTimeUtc <= upper);
        }
        var audits = await query.Where(value =>
                ((value.Outcome.StartsWith("LiveExecution:") &&
                  EF.Functions.JsonContains(value.ReasonsJson, sourceFilter)) ||
                 value.Outcome == "LiveProtectionDesired:" + compactSourceId ||
                 value.Outcome == "PaperStopSize:" + compactSourceId ||
                 value.Outcome == "TrailingStop:" + compactSourceId ||
                 (value.Outcome == "PaperPositionOpened" && EF.Functions.JsonContains(value.ReasonsJson,positionFilter)) ||
                 value.Outcome == "LiveEntryDecision:" + compactSourceId ||
                 value.Outcome == "LiveParityMissed:" + compactSourceId ||
                 value.Outcome == "LiveParityException:" + compactSourceId))
            .OrderBy(value => value.CandleTimeUtc)
            .Take(301)
            .Select(value => new LiveTradeTimelineEvent(value.CandleTimeUtc,
                value.Outcome, value.ReasonsJson))
            .ToListAsync(cancellationToken);

        var orders = new List<LiveBrokerOrderEvidence>(3);
        var limitations = new List<string>();
        if (audits.Count > 300)
        {
            audits.RemoveAt(300);
            limitations.Add("Timeline limited to the first 300 observations. Later stop confirmations or fills may be absent here; use Daily Analysis for the session.");
        }
        if (intent is null)
        {
            var observed = DailyExecutionEvidence.EntryObservation(audits.Select(a =>
                DailyExecutionEvidence.Parse(a.ObservedAtUtc,a.Event,a.EvidenceJson)).OfType<DailyExecutionEvidence.Observation>());
            if (observed is not null) limitations.Add($"No live intent. Recorded discovery evidence: {observed.Reason}");
            else if (fillAt is { } at)
            {
                var history = await DailyLiveRouteHistory.ReadAsync(db,[DailyLiveRouteHistory.Key(market)],
                    at,at.AddTicks(10),cancellationToken);
                limitations.Add(DailyLiveRouteHistory.Explain(market,at,history));
            }
            else limitations.Add("No live intent or dated paper-fill evidence. Cause unknown.");
            return new LiveTradeDiagnosticsReport(paperTradeId, sourceId, market.ToUpperInvariant(),
                position?.Strategy, position?.Status ?? "Closed", "NoLiveIntent", null,
                null, null, position?.EntryPrice ?? paper!.EntryPrice,
                position?.StopLoss, position?.Target, null, audits, orders, limitations);
        }
        var references = new List<(string Role, string Reference)>
        {
            ("Entry", intent.ClientReference), ("Protection", "LP" + intent.Id.ToString("N")[..16]),
            ("FinalExit", "LX" + sourceId.ToString("N")[..16]),
            ("PartialExit", "LQ" + sourceId.ToString("N")[..16]),
            ("AddOn", "LA" + sourceId.ToString("N")[..16]),
            ("ResidualProtection", "LR" + intent.Id.ToString("N")[..16])
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
            audits, orders, limitations)
        { StopHistory = StopComparison.Review(sourceId,audits.SelectMany(a =>
            StopComparison.Parse(a.ObservedAtUtc,a.Event,a.EvidenceJson))) };
    }
}
