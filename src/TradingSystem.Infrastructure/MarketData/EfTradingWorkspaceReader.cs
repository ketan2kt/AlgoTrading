using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TradingSystem.Application.MarketData;
using TradingSystem.Application.Execution;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Broker.Groww;
using TradingSystem.Infrastructure.Persistence;
using TradingSystem.Infrastructure.SystemStatus;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed class EfTradingWorkspaceReader(
    TradingDbContext dbContext,
    LiveNiftyFeedState feedState,
    IOptions<LiveNiftyOptions> liveOptions,
    IOptions<MarketDataOptions> marketDataOptions,
    IOptions<TradingModeOptions> tradingMode,
    IPaperAutomationReader paperAutomation,
    TimeProvider timeProvider) : ITradingWorkspaceReader
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    public async Task<TradingWorkspaceSnapshot> GetNiftyAsync(
        int candleCount,
        CancellationToken cancellationToken)
    {
        var options = liveOptions.Value;
        candleCount = Math.Clamp(candleCount, 30, options.WorkspaceCandleCount);
        var instrument = await dbContext.Instruments.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Exchange == options.Exchange &&
                                           value.TradingSymbol == options.TradingSymbol &&
                                           value.Segment == InstrumentSegment.Cash &&
                                           value.IsActive,
                cancellationToken);
        var state = feedState.GetSnapshot(TimeSpan.FromSeconds(marketDataOptions.Value.MaximumAgeSeconds));
        var now = timeProvider.GetUtcNow();
        var indiaNow = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        var localDate = indiaNow.Date;
        var sessionStartUtc = new DateTimeOffset(
            localDate.Year, localDate.Month, localDate.Day, 9, 15, 0,
            IndiaTimeZone.GetUtcOffset(localDate)).ToUniversalTime();
        var sessionEndUtc = new DateTimeOffset(
            localDate.Year, localDate.Month, localDate.Day, 15, 31, 0,
            IndiaTimeZone.GetUtcOffset(localDate)).ToUniversalTime();

        if (instrument is null)
        {
            return Empty("InstrumentUnavailable", "Synchronise the Groww Nifty instrument before enabling the feed.");
        }

        var closed = await dbContext.Candles.AsNoTracking()
            .Where(value => value.InstrumentId == instrument.Id &&
                            value.IntervalSeconds == marketDataOptions.Value.CandleIntervalSeconds &&
                            value.Source == "Groww" &&
                            value.OpenTimeUtc >= sessionStartUtc &&
                            value.OpenTimeUtc < sessionEndUtc)
            .OrderByDescending(value => value.OpenTimeUtc)
            .Take(candleCount)
            .OrderBy(value => value.OpenTimeUtc)
            .Select(value => new WorkspaceCandle(
                value.OpenTimeUtc, value.IntervalSeconds, value.Open, value.High, value.Low,
                value.Close, value.Volume, true))
            .ToListAsync(cancellationToken);

        var interval = marketDataOptions.Value.CandleIntervalSeconds;
        var currentBucket = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() -
                                                               now.ToUnixTimeSeconds() % interval);
        var observations = await dbContext.MarketObservations.AsNoTracking()
            .Where(value => value.InstrumentId == instrument.Id &&
                            value.Source == "Groww" &&
                            value.SourceTimestampUtc >= currentBucket)
            .OrderBy(value => value.SourceTimestampUtc)
            .ToListAsync(cancellationToken);
        if (observations.Count > 0)
        {
            closed.Add(new WorkspaceCandle(
                currentBucket,
                interval,
                observations[0].Price,
                observations.Max(value => value.Price),
                observations.Min(value => value.Price),
                observations[^1].Price,
                observations.Sum(value => value.VolumeDelta),
                false));
        }

        var signalRows = await dbContext.Signals.AsNoTracking()
            .Where(value => value.InstrumentId == instrument.Id)
            .OrderByDescending(value => value.MarketDataTimestampUtc)
            .Take(30)
            .Select(value => new
            {
                value.Id,
                value.Direction,
                value.MarketDataTimestampUtc,
                value.ProposedEntry,
                value.ProposedStopLoss,
                value.ProposedTarget,
                value.Status
            })
            .ToListAsync(cancellationToken);
        var signalIds = signalRows.Select(value => value.Id).ToArray();
        var risk = await dbContext.RiskDecisions.AsNoTracking()
            .Where(value => signalIds.Contains(value.SignalId))
            .ToDictionaryAsync(value => value.SignalId, cancellationToken);
        var entryReferences = signalIds.Select(value => $"{value:N}-ENTRY").ToArray();
        var brokerEvents = await dbContext.PaperBrokerEvents.AsNoTracking()
            .Where(value => entryReferences.Contains(value.ClientReference) &&
                            (value.EventType == "OrderSubmitted" || value.EventType == "OrderFilled"))
            .OrderByDescending(value => value.Sequence)
            .Select(value => new { value.ClientReference, value.PayloadJson })
            .ToListAsync(cancellationToken);
        var orders = brokerEvents
            .GroupBy(value => value.ClientReference, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => ParsePaperOrder(group.Select(value => value.PayloadJson)),
                StringComparer.Ordinal);
        var executionInstrumentIds = orders.Values.Where(value => value is not null)
            .Select(value => value!.InstrumentId).Distinct().ToArray();
        var executionInstruments = await dbContext.Instruments.AsNoTracking()
            .Where(value => executionInstrumentIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, cancellationToken);
        var overlays = signalRows.Select(value =>
        {
            risk.TryGetValue(value.Id, out var decision);
            orders.TryGetValue($"{value.Id:N}-ENTRY", out var order);
            var executionInstrument = order is null ? null :
                executionInstruments.GetValueOrDefault(order.InstrumentId);
            var protective = ParseProtectivePrices(decision?.SnapshotJson);
            return new WorkspaceTradeOverlay(
                value.Id,
                "Opening Range Breakout 1.0.0",
                value.Direction.ToString(),
                value.MarketDataTimestampUtc,
                value.ProposedEntry,
                value.ProposedStopLoss,
                value.ProposedTarget,
                order?.FillPrice is not null ? "Filled" : decision is null
                    ? value.Status.ToString()
                    : decision.Approved ? "RiskApproved" : "RiskRejected",
                order?.Quantity ?? decision?.ApprovedQuantity,
                order?.FillPrice,
                executionInstrument?.TradingSymbol,
                executionInstrument?.Type.ToString(),
                executionInstrument?.ExpiryDate,
                executionInstrument?.StrikePrice,
                protective.StopLoss,
                protective.Target);
        }).ToArray();

        var message = !options.Enabled
            ? "Live Nifty ingestion is disabled by server configuration."
            : state.Message;
        return new TradingWorkspaceSnapshot(
            options.TradingSymbol,
            options.Exchange,
            $"{interval / 60}m",
            tradingMode.Value.Mode.ToString(),
            state.Status,
            options.Enabled && state.Status == "Connected",
            state.IsFresh,
            state.LastMarketTimestampUtc,
            now,
            message,
            closed,
            overlays,
            paperAutomation.GetCurrent());

        TradingWorkspaceSnapshot Empty(string status, string detail) => new(
            options.TradingSymbol,
            options.Exchange,
            $"{marketDataOptions.Value.CandleIntervalSeconds / 60}m",
            tradingMode.Value.Mode.ToString(),
            status,
            false,
            false,
            state.LastMarketTimestampUtc,
            now,
            detail,
            [],
            [],
            paperAutomation.GetCurrent());
    }

    private static PaperOrderProjection? ParsePaperOrder(IEnumerable<string> payloads)
    {
        Guid? instrumentId = null;
        int? quantity = null;
        decimal? fillPrice = null;
        foreach (var payloadJson in payloads)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.TryGetProperty("instrumentId", out var instrument) &&
                instrument.TryGetGuid(out var parsedInstrument))
                instrumentId = parsedInstrument;
            if (root.TryGetProperty("cumulativeFilledQuantity", out var filledQuantity) &&
                filledQuantity.TryGetInt32(out var parsedQuantity))
                quantity = parsedQuantity;
            if (root.TryGetProperty("averageFillPrice", out var averageFillPrice) &&
                averageFillPrice.TryGetDecimal(out var parsedPrice))
                fillPrice = parsedPrice;
        }
        return instrumentId is null ? null : new(instrumentId.Value, quantity, fillPrice);
    }

    private static (decimal? StopLoss, decimal? Target) ParseProtectivePrices(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return (null, null);
        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;
        var stop = root.TryGetProperty("finalStopLoss", out var stopElement) &&
                   stopElement.TryGetDecimal(out var parsedStop) ? parsedStop : (decimal?)null;
        var target = root.TryGetProperty("finalTarget", out var targetElement) &&
                     targetElement.TryGetDecimal(out var parsedTarget) ? parsedTarget : (decimal?)null;
        return (stop, target);
    }

    private sealed record PaperOrderProjection(Guid InstrumentId, int? Quantity, decimal? FillPrice);

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
    }
}
