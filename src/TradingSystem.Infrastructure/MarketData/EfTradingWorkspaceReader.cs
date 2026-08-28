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
    MultiMarketFeedState multiFeedState,
    IOptions<LiveNiftyOptions> liveOptions,
    IOptions<MarketDataOptions> marketDataOptions,
    IOptions<TradingModeOptions> tradingMode,
    IPaperAutomationReader paperAutomation,
    TimeProvider timeProvider) : ITradingWorkspaceReader
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    public async Task<TradingWorkspaceSnapshot> GetNiftyAsync(
        int candleCount,
        CancellationToken cancellationToken) =>
        await GetAsync("nifty", candleCount, cancellationToken);

    public async Task<TradingWorkspaceSnapshot> GetAsync(
        string market,
        int candleCount,
        CancellationToken cancellationToken)
    {
        var options = liveOptions.Value;
        var definition = TradingMarketCatalog.Get(market);
        candleCount = Math.Clamp(candleCount, 30, options.WorkspaceCandleCount);
        var now = timeProvider.GetUtcNow();
        var indiaNow = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        var sessionDate = DateOnly.FromDateTime(indiaNow.Date);
        var instrumentQuery = ScopeInstrumentQuery(dbContext.Instruments.AsNoTracking(), definition);
        var instrument = definition.InstrumentType == InstrumentType.Index
            ? await instrumentQuery.FirstOrDefaultAsync(value => value.TradingSymbol == definition.UnderlyingSymbol,
                cancellationToken)
            : await instrumentQuery.Where(value => value.TradingSymbol.StartsWith(definition.UnderlyingSymbol) &&
                                                   value.ExpiryDate >= sessionDate)
                .OrderBy(value => value.ExpiryDate).ThenBy(value => value.TradingSymbol)
                .FirstOrDefaultAsync(cancellationToken);
        var state = market == "nifty"
            ? feedState.GetSnapshot(TimeSpan.FromSeconds(marketDataOptions.Value.MaximumAgeSeconds))
            : multiFeedState.GetSnapshot(market, TimeSpan.FromSeconds(marketDataOptions.Value.MaximumAgeSeconds));
        var localDate = indiaNow.Date;
        var sessionStartUtc = new DateTimeOffset(
            localDate.Year, localDate.Month, localDate.Day, definition.SessionStart.Hour, definition.SessionStart.Minute, 0,
            IndiaTimeZone.GetUtcOffset(localDate)).ToUniversalTime();
        var sessionEndUtc = new DateTimeOffset(
            localDate.Year, localDate.Month, localDate.Day, definition.SessionEnd.Hour, definition.SessionEnd.Minute, 59,
            IndiaTimeZone.GetUtcOffset(localDate)).ToUniversalTime();

        if (instrument is null)
        {
            return Empty("InstrumentUnavailable", $"Synchronise the Groww {definition.DisplayName} instrument before enabling the feed.");
        }

        var closed = await dbContext.Candles.AsNoTracking()
            .Where(value => value.InstrumentId == instrument.Id &&
                            value.IntervalSeconds == marketDataOptions.Value.CandleIntervalSeconds &&
                            value.Source == "Groww" &&
                            value.OpenTimeUtc >= sessionStartUtc.AddDays(-7) &&
                            value.OpenTimeUtc < sessionEndUtc)
            .OrderByDescending(value => value.OpenTimeUtc)
            .Take(candleCount)
            .OrderBy(value => value.OpenTimeUtc)
            .Select(value => new WorkspaceCandle(
                value.OpenTimeUtc, value.IntervalSeconds, value.Open, value.High, value.Low,
                value.Close, value.Volume, true))
            .ToListAsync(cancellationToken);
        var displayedSessionDates = closed
            .Select(value => DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(value.OpenTimeUtc, IndiaTimeZone).Date))
            .Distinct()
            .OrderByDescending(value => value)
            .Take(3)
            .ToHashSet();
        closed = closed.Where(value => displayedSessionDates.Contains(DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(value.OpenTimeUtc, IndiaTimeZone).Date))).ToList();

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

        var volumeInstrument = definition.InstrumentType == InstrumentType.Future ? instrument :
            await dbContext.Instruments.AsNoTracking()
            .Where(value => value.Exchange == definition.Exchange && value.Type == InstrumentType.Future &&
                            value.TradingSymbol.StartsWith(definition.ExecutionUnderlying) && value.IsActive &&
                            value.ExpiryDate >= sessionDate)
            .OrderBy(value => value.ExpiryDate)
            .ThenBy(value => value.TradingSymbol)
            .FirstOrDefaultAsync(cancellationToken);
        var futuresVolume = volumeInstrument is null
            ? []
            : await dbContext.Candles.AsNoTracking()
                .Where(value => value.InstrumentId == volumeInstrument.Id &&
                                value.IntervalSeconds == interval && value.Source == "Groww" &&
                                value.OpenTimeUtc >= sessionStartUtc.AddDays(-7) &&
                                value.OpenTimeUtc < sessionEndUtc)
                .OrderBy(value => value.OpenTimeUtc)
                .Select(value => new WorkspaceVolumeBar(value.OpenTimeUtc, value.Volume, true))
                .ToListAsync(cancellationToken);
        if (displayedSessionDates.Count > 0)
            futuresVolume = futuresVolume.Where(value => displayedSessionDates.Contains(
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.OpenTimeUtc, IndiaTimeZone).Date)))
                .ToList();

        var signalRows = market == TradingMarketCatalog.Nifty.Code
            ? await (from value in dbContext.Signals.AsNoTracking()
            join version in dbContext.StrategyVersions.AsNoTracking()
                on value.StrategyVersionId equals version.Id
            join strategy in dbContext.Strategies.AsNoTracking()
                on version.StrategyId equals strategy.Id
            where value.InstrumentId == instrument.Id &&
                  value.MarketDataTimestampUtc >= sessionStartUtc &&
                  value.MarketDataTimestampUtc < sessionEndUtc
            orderby value.MarketDataTimestampUtc descending
            select new
            {
                value.Id,
                value.Direction,
                value.MarketDataTimestampUtc,
                value.ProposedEntry,
                value.ProposedStopLoss,
                value.ProposedTarget,
                value.Status,
                Strategy = strategy.Code + " " + version.Version
            })
            .Take(30)
            .ToListAsync(cancellationToken)
            : [];
        var signalIds = signalRows.Select(value => value.Id).ToArray();
        var risk = await dbContext.RiskDecisions.AsNoTracking()
            .Where(value => signalIds.Contains(value.SignalId))
            .ToDictionaryAsync(value => value.SignalId, cancellationToken);
        var entryReferences = signalIds.Select(value => $"{value:N}-ENTRY").ToArray();
        var brokerEvents = await dbContext.PaperBrokerEvents.AsNoTracking()
            .Where(value => entryReferences.Contains(value.ClientReference) &&
                            (value.EventType == "OrderSubmitted" || value.EventType == "OrderFilled"))
            .OrderByDescending(value => value.Sequence)
            .Select(value => new PaperBrokerEventProjection(
                value.ClientReference, value.EventType, value.PayloadJson, value.OccurredAtUtc))
            .ToListAsync(cancellationToken);
        var orders = brokerEvents
            .GroupBy(value => value.ClientReference, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => ParsePaperOrder(group),
                StringComparer.Ordinal);
        var executionInstrumentIds = orders.Values.Where(value => value is not null)
            .Select(value => value!.InstrumentId).Distinct().ToArray();
        var executionInstruments = await dbContext.Instruments.AsNoTracking()
            .Where(value => executionInstrumentIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, cancellationToken);
        var tradeResults = await dbContext.PaperTradeResults.AsNoTracking()
            .Where(value => signalIds.Contains(value.SignalId))
            .ToDictionaryAsync(value => value.SignalId, cancellationToken);
        var automation = paperAutomation.GetCurrent();
        var legacyOverlays = signalRows.Select(value =>
        {
            risk.TryGetValue(value.Id, out var decision);
            orders.TryGetValue($"{value.Id:N}-ENTRY", out var order);
            var executionInstrument = order is null ? null :
                executionInstruments.GetValueOrDefault(order.InstrumentId);
            var execution = ParseExecutionDecision(decision?.SnapshotJson);
            var rejectionReasons = ParseRejectionReasons(decision?.ReasonsJson);
            tradeResults.TryGetValue(value.Id, out var result);
            var positionMark = automation.ActivePositionMarks?
                .FirstOrDefault(mark => mark.SignalId == value.Id);
            var isActive = result is null && positionMark is not null;
            var lifecycleStatus = result is not null ? FormatExitStatus(result.ExitReason) :
                isActive ? (positionMark!.QuoteAvailable ? "Active" : "Active · quote unavailable") :
                order?.FillPrice is not null ? "Entry filled" : decision is null
                    ? value.Status.ToString()
                    : decision.Approved ? "Risk approved" : "Risk rejected";
            return new WorkspaceTradeOverlay(
                value.Id,
                value.Strategy,
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
                executionInstrument?.TradingSymbol ?? execution.TradingSymbol,
                executionInstrument?.Type.ToString() ?? execution.InstrumentType,
                executionInstrument?.ExpiryDate ?? execution.ExpiryDate,
                executionInstrument?.StrikePrice ?? execution.StrikePrice,
                executionInstrument?.LotSize ?? execution.LotSize,
                execution.MaximumLots,
                execution.ProposedEntry,
                execution.ProposedEntry is { } proposedEntry && execution.StopLoss is { } proposedStop &&
                (executionInstrument?.LotSize ?? execution.LotSize) is { } executionLotSize
                    ? Math.Abs(proposedEntry - proposedStop) * executionLotSize
                    : null,
                execution.StopLoss,
                execution.Target,
                execution.RiskAmount,
                execution.CapitalExposure,
                rejectionReasons,
                lifecycleStatus,
                isActive ? positionMark!.CurrentPrice : result?.ExitPrice,
                result?.ExitPrice,
                result?.RealisedPnl,
                isActive ? positionMark!.UnrealisedPnl : null,
                order?.FillTimeUtc,
                result?.ClosedAtUtc);
        }).ToArray();
        var positionHistoryStartUtc = PositionHistoryStartUtc(definition, sessionStartUtc);
        var marketPositionRows = await dbContext.MarketPaperPositions.AsNoTracking()
            .Where(value => value.Market == definition.Code && value.OpenedAtUtc >= positionHistoryStartUtc &&
                            value.OpenedAtUtc < sessionEndUtc)
            .OrderByDescending(value => value.OpenedAtUtc).Take(30).ToListAsync(cancellationToken);
        var marketExecutionIds = marketPositionRows.Select(value => value.ExecutionInstrumentId).Distinct().ToArray();
        var marketExecutionInstruments = await dbContext.Instruments.AsNoTracking()
            .Where(value => marketExecutionIds.Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken);
        var marketOverlays = marketPositionRows.Select(value =>
        {
            marketExecutionInstruments.TryGetValue(value.ExecutionInstrumentId, out var execution);
            var isActive = value.Status == "Active";
            var underlyingPrice = closed.LastOrDefault()?.Close ?? value.EntryPrice;
            return new WorkspaceTradeOverlay(value.Id, value.Strategy, value.Direction.ToString(),
                value.OpenedAtUtc, underlyingPrice, underlyingPrice, underlyingPrice,
                isActive ? "Filled" : "Closed", value.Quantity, value.EntryPrice,
                execution?.TradingSymbol ?? definition.ExecutionUnderlying,
                execution?.Type.ToString() ?? definition.InstrumentType.ToString(), execution?.ExpiryDate,
                execution?.StrikePrice, execution?.LotSize,
                execution is { LotSize: > 0 } ? value.Quantity / execution.LotSize : null,
                value.EntryPrice, execution is null ? null :
                    Math.Abs(value.EntryPrice - value.StopLoss) * execution.LotSize,
                value.StopLoss, value.Target, Math.Abs(value.EntryPrice - value.StopLoss) * value.Quantity,
                value.EntryPrice * value.Quantity, [], value.Status, value.CurrentPrice,
                isActive ? null : value.CurrentPrice, isActive ? null : value.RealisedPnl,
                isActive ? (value.Direction == Direction.Buy ? value.CurrentPrice - value.EntryPrice :
                    value.EntryPrice - value.CurrentPrice) * value.Quantity : null,
                value.OpenedAtUtc, value.ClosedAtUtc);
        }).ToArray();
        var overlays = market == "nifty"
            ? legacyOverlays.Concat(marketOverlays.Where(value =>
                value.Strategy.StartsWith("Hero Zero|", StringComparison.Ordinal))).ToArray()
            : marketOverlays;
        if (market != "nifty")
        {
            var todaysPositions = marketPositionRows.Where(value => value.OpenedAtUtc >= sessionStartUtc &&
                                                                    value.OpenedAtUtc < sessionEndUtc).ToArray();
            var activePosition = todaysPositions.FirstOrDefault(value => value.Status == "Active");
            var realised = todaysPositions.Where(value => value.Status != "Active").Sum(value => value.RealisedPnl);
            var unrealised = todaysPositions.Where(value => value.Status == "Active").Sum(value =>
                (value.Direction == Direction.Buy ? value.CurrentPrice - value.EntryPrice :
                    value.EntryPrice - value.CurrentPrice) * value.Quantity);
            automation = new PaperAutomationSnapshot(state.IsFresh ? "Scanning" : "DataUnavailable",
                state.IsFresh, state.IsFresh ? $"{definition.DisplayName} paper engine is scanning completed candles."
                    : state.Message ?? "Live data is unavailable.", now, todaysPositions.Length, realised, unrealised,
                activePosition?.Id, activePosition?.Direction.ToString(), activePosition?.Quantity,
                activePosition?.EntryPrice, activePosition?.StopLoss, activePosition?.Target,
                ActiveExecutionInstrument(activePosition)?.TradingSymbol ??
                    (activePosition is null ? null : definition.ExecutionUnderlying),
                ActiveExecutionInstrument(activePosition)?.Type.ToString() ??
                    (activePosition is null ? null : definition.InstrumentType.ToString()),
                ActiveExecutionInstrument(activePosition)?.ExpiryDate,
                ActiveExecutionInstrument(activePosition)?.StrikePrice,
                ActiveExecutionInstrument(activePosition)?.LotSize,
                [new("feed", $"{definition.DisplayName} live feed", state.IsFresh,
                    state.IsFresh ? "Current quote available" : state.Message ?? "Waiting for live quote")],
                activePosition?.CurrentPrice,
                todaysPositions.Where(value => value.Status == "Active").Select(value => new PaperPositionMark(
                    value.Id, value.CurrentPrice, value.CurrentPrice,
                    (value.Direction == Direction.Buy ? value.CurrentPrice - value.EntryPrice :
                    value.EntryPrice - value.CurrentPrice) * value.Quantity, now, true)).ToArray());

            Instrument? ActiveExecutionInstrument(MarketPaperPosition? position) =>
                position is not null && marketExecutionInstruments.TryGetValue(position.ExecutionInstrumentId,
                    out var resolved) ? resolved : null;
        }

        var evaluationRows = market == TradingMarketCatalog.Nifty.Code
            ? await dbContext.StrategyEvaluations.AsNoTracking()
            .Where(value => value.InstrumentId == instrument.Id &&
                            value.CandleTimeUtc >= sessionStartUtc &&
                            value.CandleTimeUtc < sessionEndUtc)
            .OrderByDescending(value => value.CandleTimeUtc)
            .Take(40)
            .ToListAsync(cancellationToken)
            : [];
        var legacyEvaluations = evaluationRows.Select(value => new WorkspaceStrategyEvaluation(
            value.Id,
            value.CandleTimeUtc,
            $"{value.StrategyCode} {value.StrategyVersion}",
            value.Outcome,
            value.CurrentPrice,
            value.OpeningRangeHigh,
            value.OpeningRangeLow,
            value.Vwap,
            value.FastEma,
            value.SlowEma,
            value.AtrPercent,
            value.RelativeFuturesVolume,
            value.Regime.ToString(),
            value.RegimeBias?.ToString(),
            value.RegimeConfidence,
            ParseRejectionReasons(value.FailedConditionsJson),
            value.SignalId,
            value.OptionSymbol,
            value.OptionType,
            value.OptionExpiry,
            value.OptionStrike,
            value.OptionPremium,
            value.SignalId is { } signalId && tradeResults.TryGetValue(signalId, out var result)
                ? result.RealisedPnl : null,
            value.ShadowStructureState,
            value.ShadowTrendQuality,
            value.ShadowWouldPermit,
            ParseRejectionReasons(value.ShadowEvidenceJson))).ToArray();
        var marketAudits = market == "nifty" ? [] : await dbContext.MarketStrategyAudits.AsNoTracking()
            .Where(value => value.Market == definition.Code && value.CandleTimeUtc >= sessionStartUtc &&
                            value.CandleTimeUtc < sessionEndUtc &&
                            !value.Outcome.StartsWith("PositionSample:") &&
                            !value.Outcome.StartsWith("TrailingStop:") &&
                            !value.Outcome.StartsWith("PostExit"))
            .OrderByDescending(value => value.CandleTimeUtc).Take(40).ToListAsync(cancellationToken);
        var evaluations = market == "nifty" ? legacyEvaluations : marketAudits.Select(value =>
            new WorkspaceStrategyEvaluation(value.Id, value.CandleTimeUtc,
                "Multi-market momentum breakout 1.0.0", value.Outcome,
                closed.LastOrDefault()?.Close ?? 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m,
                "MarketStructure", null, value.Confidence, ParseRejectionReasons(value.ReasonsJson),
                null, null, null, null, null, null, null, null, null, null, [])).ToArray();

        var message = !options.Enabled
            ? $"Live {definition.DisplayName} ingestion is disabled by server configuration."
            : state.Message;
        return new TradingWorkspaceSnapshot(
            instrument.TradingSymbol,
            definition.Exchange,
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
            evaluations,
            automation,
            futuresVolume);

        TradingWorkspaceSnapshot Empty(string status, string detail) => new(
            definition.UnderlyingSymbol,
            definition.Exchange,
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
            [],
            market == TradingMarketCatalog.Nifty.Code
                ? paperAutomation.GetCurrent()
                : CreateUnavailableAutomation(definition, status, detail, now));
    }

    internal static PaperAutomationSnapshot CreateUnavailableAutomation(
        TradingMarketDefinition definition,
        string status,
        string detail,
        DateTimeOffset observedAtUtc) => new(
            status,
            false,
            detail,
            observedAtUtc,
            0,
            0m,
            0m,
            null,
            null,
            null,
            null,
            null,
            null,
            ReadinessChecks:
            [
                new PaperReadinessCheck("feed", $"{definition.DisplayName} live feed", false, detail)
            ],
            ActivePositionMarks: []);

    private static string FormatExitStatus(string exitReason) => exitReason switch
    {
        "StopLoss" => "SL hit",
        "Target" => "Target hit",
        "ForcedIntradayExit" => "Time exit",
        "EmergencyKillSwitch" => "Emergency exit",
        "UnderlyingTrendInvalidated" => "Trend reversal exit",
        _ => "Closed"
    };

    internal static DateTimeOffset PositionHistoryStartUtc(
        TradingMarketDefinition definition,
        DateTimeOffset sessionStartUtc) => sessionStartUtc;

    internal static IQueryable<Instrument> ScopeInstrumentQuery(
        IQueryable<Instrument> instruments,
        TradingMarketDefinition definition) => instruments.Where(value =>
        value.Exchange == definition.Exchange &&
        value.Segment == definition.InstrumentSegment &&
        value.Type == definition.InstrumentType &&
        value.IsActive);

    private static PaperOrderProjection? ParsePaperOrder(IEnumerable<PaperBrokerEventProjection> events)
    {
        Guid? instrumentId = null;
        int? quantity = null;
        decimal? fillPrice = null;
        DateTimeOffset? fillTimeUtc = null;
        foreach (var entry in events)
        {
            using var document = JsonDocument.Parse(entry.PayloadJson);
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
            if (entry.EventType == "OrderFilled") fillTimeUtc = entry.OccurredAtUtc;
        }
        return instrumentId is null ? null : new(instrumentId.Value, quantity, fillPrice, fillTimeUtc);
    }

    private static ExecutionDecisionProjection ParseExecutionDecision(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return new();
        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;
        var stop = root.TryGetProperty("finalStopLoss", out var stopElement) &&
                   stopElement.TryGetDecimal(out var parsedStop) ? parsedStop : (decimal?)null;
        var target = root.TryGetProperty("finalTarget", out var targetElement) &&
                     targetElement.TryGetDecimal(out var parsedTarget) ? parsedTarget : (decimal?)null;
        var riskAmount = root.TryGetProperty("riskAmount", out var riskElement) &&
                         riskElement.TryGetDecimal(out var parsedRisk) ? parsedRisk : (decimal?)null;
        var exposure = root.TryGetProperty("capitalExposure", out var exposureElement) &&
                       exposureElement.TryGetDecimal(out var parsedExposure) ? parsedExposure : (decimal?)null;
        if (!root.TryGetProperty("optionProposal", out var proposal) ||
            proposal.ValueKind != JsonValueKind.Object)
            return new(StopLoss: stop, Target: target, RiskAmount: riskAmount,
                CapitalExposure: exposure);
        return new(
            proposal.TryGetProperty("tradingSymbol", out var symbol) ? symbol.GetString() : null,
            proposal.TryGetProperty("instrumentType", out var type) ? type.GetString() : null,
            proposal.TryGetProperty("expiryDate", out var expiry) &&
            DateOnly.TryParse(expiry.GetString(), out var parsedExpiry) ? parsedExpiry : null,
            proposal.TryGetProperty("strikePrice", out var strike) && strike.TryGetDecimal(out var parsedStrike)
                ? parsedStrike : null,
            proposal.TryGetProperty("lotSize", out var lot) && lot.TryGetInt32(out var parsedLot)
                ? parsedLot : null,
            proposal.TryGetProperty("maximumLots", out var maximumLots) &&
            maximumLots.TryGetInt32(out var parsedMaximumLots) ? parsedMaximumLots : null,
            proposal.TryGetProperty("proposedEntry", out var entry) && entry.TryGetDecimal(out var parsedEntry)
                ? parsedEntry : null,
            stop,
            target,
            riskAmount,
            exposure);
    }

    internal static string[] ParseRejectionReasons(string? reasonsJson)
    {
        if (string.IsNullOrWhiteSpace(reasonsJson)) return [];
        try
        {
            using var document = JsonDocument.Parse(reasonsJson);
            var root = document.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.Array => root.EnumerateArray()
                    .Select(value => value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : value.GetRawText())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray(),
                JsonValueKind.Object => root.EnumerateObject()
                    .Select(value => $"{value.Name}: {FormatReasonValue(value.Value)}")
                    .ToArray(),
                JsonValueKind.String => [root.GetString()!],
                JsonValueKind.Null or JsonValueKind.Undefined => [],
                _ => [root.GetRawText()]
            };
        }
        catch (JsonException)
        {
            return [reasonsJson];
        }
    }

    private static string FormatReasonValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private sealed record PaperBrokerEventProjection(string ClientReference, string EventType,
        string PayloadJson, DateTimeOffset OccurredAtUtc);
    private sealed record PaperOrderProjection(Guid InstrumentId, int? Quantity, decimal? FillPrice,
        DateTimeOffset? FillTimeUtc);
    private sealed record ExecutionDecisionProjection(
        string? TradingSymbol = null,
        string? InstrumentType = null,
        DateOnly? ExpiryDate = null,
        decimal? StrikePrice = null,
        int? LotSize = null,
        int? MaximumLots = null,
        decimal? ProposedEntry = null,
        decimal? StopLoss = null,
        decimal? Target = null,
        decimal? RiskAmount = null,
        decimal? CapitalExposure = null);

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
