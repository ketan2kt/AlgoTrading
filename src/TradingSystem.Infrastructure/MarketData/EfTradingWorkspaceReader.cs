using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TradingSystem.Application.MarketData;
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
        var fillEvents = await dbContext.PaperBrokerEvents.AsNoTracking()
            .Where(value => entryReferences.Contains(value.ClientReference) &&
                            value.EventType == "OrderFilled")
            .OrderByDescending(value => value.Sequence)
            .Select(value => new { value.ClientReference, value.PayloadJson })
            .ToListAsync(cancellationToken);
        var fills = fillEvents
            .GroupBy(value => value.ClientReference, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => ParseFill(group.First().PayloadJson),
                StringComparer.Ordinal);
        var overlays = signalRows.Select(value =>
        {
            risk.TryGetValue(value.Id, out var decision);
            fills.TryGetValue($"{value.Id:N}-ENTRY", out var fill);
            return new WorkspaceTradeOverlay(
                value.Id,
                "Opening Range Breakout 1.0.0",
                value.Direction.ToString(),
                value.MarketDataTimestampUtc,
                value.ProposedEntry,
                value.ProposedStopLoss,
                value.ProposedTarget,
                fill is not null ? "Filled" : decision is null
                    ? value.Status.ToString()
                    : decision.Approved ? "RiskApproved" : "RiskRejected",
                fill?.Quantity ?? decision?.ApprovedQuantity,
                fill?.Price);
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
            overlays);

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
            []);
    }

    private static FillProjection? ParseFill(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("cumulativeFilledQuantity", out var quantity) ||
            !root.TryGetProperty("averageFillPrice", out var price) ||
            !quantity.TryGetInt32(out var parsedQuantity) ||
            !price.TryGetDecimal(out var parsedPrice))
        {
            return null;
        }

        return new(parsedQuantity, parsedPrice);
    }

    private sealed record FillProjection(int Quantity, decimal Price);

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
