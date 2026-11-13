using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[EnableRateLimiting("diagnostics")]
[Route("api/research/historical-replay")]
[HistoricalReplayWorkWindow]
public sealed class HistoricalReplayController(IHistoricalReplayReportReader reader,
    IHistoricalReplayArchiveReader archiveReader,
    IGrowwReadOnlyGateway groww,
    GrowwHistoricalResearchImporter researchImporter,
    TradingDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet("cohort-coverage")]
    public async Task<ActionResult<HistoricalReplayCohortRangeReport>> CohortCoverage(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to,
        CancellationToken cancellationToken, [FromQuery] bool allowModeledHistoricalCosts = false)
    {
        Response.Headers.CacheControl = "no-store";
        if (from == default || to == default || from > to || !HistoricalReplaySessionCalendar.IsCompletedDate(to, clock.GetUtcNow()) ||
            from.Year < 2020 || to.DayNumber - from.DayNumber > 30)
            return BadRequest("Select a completed date range of up to 31 days.");
        string[] markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
        var dates = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(from.AddDays).Where(value => value.DayOfWeek is not
                (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();
        // A pre-open dated master is mandatory for full replay. Avoid three
        // expensive per-market archive reads for old dates that lack it.
        var masterDates = (await db.HistoricalInstrumentMasterSnapshots.AsNoTracking()
            .Where(value => value.SessionDate >= from && value.SessionDate <= to &&
                value.Source == "GrowwInstrumentMasterV1")
            .Select(value => new { value.SessionDate, value.CapturedAtUtc })
            .ToArrayAsync(cancellationToken))
            .Where(value => value.CapturedAtUtc <= new DateTimeOffset(
                value.SessionDate.ToDateTime(new TimeOnly(9, 15)),
                TimeSpan.FromHours(5.5)).ToUniversalTime())
            .Select(value => value.SessionDate).ToHashSet();
        var sessions = new List<HistoricalReplaySessionData>();
        var unreadable = new Dictionary<DateOnly, List<string>>();
        foreach (var date in dates)
        {
            if (!masterDates.Contains(date))
            {
                unreadable[date] = ["Pre-open dated instrument master missing"];
                continue;
            }
            foreach (var (market, route) in new[]
                     { ("NIFTY", "nifty"), ("SENSEX", "sensex"),
                       ("BANKNIFTY", "bank-nifty") })
            {
                try
                {
                    var slice = await archiveReader.ReadStrictAsync(date, route,
                        cancellationToken);
                    sessions.Add(slice.Session);
                }
                catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or JsonException)
                {
                    if (!unreadable.TryGetValue(date, out var reasons))
                        unreadable[date] = reasons = [];
                    reasons.Add($"{market}: archive unavailable or invalid");
                }
            }
        }
        var selection = HistoricalReplayCohortSelector.Select(sessions,
            markets, dates, allowModeledHistoricalCosts, cancellationToken);
        var excluded = selection.ExcludedDates.Select(value => new
            HistoricalReplayExcludedDate(value.Date,
                value.Reasons.Concat(unreadable.GetValueOrDefault(value.Date) ?? [])
                    .Distinct(StringComparer.Ordinal).ToArray())).ToArray();
        return Ok(new HistoricalReplayCohortRangeReport(from, to,
            selection.IncludedSessions.Select(value => value.Date).Distinct()
                .OrderBy(value => value).ToArray(), excluded));
    }

    [HttpGet("archive-coverage")]
    public async Task<ActionResult<HistoricalReplayArchiveCoverage>> ArchiveCoverage(
        [FromQuery] DateOnly date, [FromQuery] string market,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var normalized = market?.Trim().ToLowerInvariant() ?? string.Empty;
        if (date == default || !HistoricalReplaySessionCalendar.IsCompletedDate(date, clock.GetUtcNow()) || date.Year < 2020 ||
            normalized is not ("nifty" or "sensex" or "bank-nifty"))
            return BadRequest("Choose a completed day and supported market.");
        var slice = await archiveReader.ReadAsync(date, normalized, cancellationToken);
        return Ok(HistoricalReplayArchiveCoverageEvaluator.Evaluate(slice,
            normalized));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("backfill-index-session")]
    public async Task<ActionResult<HistoricalIndexBackfillResult>> BackfillIndexSession(
        HistoricalIndexBackfillRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var indiaNow = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
        var today = DateOnly.FromDateTime(indiaNow.DateTime);
        var market = request.Market?.Trim().ToLowerInvariant() ?? string.Empty;
        var (exchange, underlying) = market switch
        {
            "nifty" => ("NSE", "NIFTY"),
            "sensex" => ("BSE", "SENSEX"),
            "bank-nifty" => ("NSE", "BANKNIFTY"),
            _ => (string.Empty, string.Empty)
        };
        if (underlying.Length == 0 || request.Date >= today || request.Date.Year < 2020)
            return BadRequest("Choose a completed session and supported market.");
        var localTime = TimeOnly.FromDateTime(indiaNow.DateTime);
        if (indiaNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
            localTime >= new TimeOnly(9, 0) && localTime <= new TimeOnly(15, 45))
            return Conflict("Research backfill is available outside market hours only.");
        var instrument = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(value =>
            value.Exchange == exchange && value.TradingSymbol == underlying &&
            value.Segment == InstrumentSegment.Cash &&
            value.Type == InstrumentType.Index, cancellationToken);
        if (instrument is null || string.IsNullOrWhiteSpace(instrument.GrowwSymbol))
            return Conflict("The index instrument lacks verified Groww metadata.");
        var start = new DateTimeOffset(request.Date.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var end = new DateTimeOffset(request.Date.ToDateTime(new TimeOnly(15, 30)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var history = await groww.GetHistoricalCandlesAsync(new(exchange, "CASH",
            instrument.GrowwSymbol, start, end, "1minute"), cancellationToken);
        var added = await researchImporter.ImportMappedAsync(instrument, underlying, request.Date, history,
            start, end, clock.GetUtcNow(), cancellationToken);
        var times = await db.HistoricalResearchCandles.AsNoTracking().Where(value =>
                value.GrowwSymbol == instrument.GrowwSymbol && value.IntervalSeconds == 60 &&
                value.OpenTimeUtc >= start && value.OpenTimeUtc < end)
            .Select(value => value.OpenTimeUtc).ToArrayAsync(cancellationToken);
        var coverage = HistoricalSessionCoverageEvaluator.Evaluate(request.Date, times);
        return Ok(new HistoricalIndexBackfillResult(request.Date, market, added,
            coverage.PresentMinutes, coverage.ExpectedMinutes, coverage.MissingMinutes));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("backfill-front-future")]
    public async Task<ActionResult<HistoricalFutureBackfillResult>> BackfillFrontFuture(
        HistoricalIndexBackfillRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var indiaNow = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
        var today = DateOnly.FromDateTime(indiaNow.DateTime);
        var market = request.Market?.Trim().ToLowerInvariant() ?? string.Empty;
        var (exchange, underlying) = market switch
        {
            "nifty" => ("NSE", "NIFTY"),
            "sensex" => ("BSE", "SENSEX"),
            "bank-nifty" => ("NSE", "BANKNIFTY"),
            _ => (string.Empty, string.Empty)
        };
        if (underlying.Length == 0 || request.Date >= today || request.Date.Year < 2020)
            return BadRequest("Choose a completed session and supported market.");
        var localTime = TimeOnly.FromDateTime(indiaNow.DateTime);
        if (indiaNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
            localTime >= new TimeOnly(9, 0) && localTime <= new TimeOnly(15, 45))
            return Conflict("Research backfill is available outside market hours only.");
        var last = request.Date.AddDays(60);
        var month = new DateOnly(request.Date.Year, request.Date.Month, 1);
        var expiries = new List<DateOnly>();
        while (month <= last)
        {
            expiries.AddRange(await groww.GetHistoricalExpiriesAsync(exchange,
                underlying, month.Year, month.Month, cancellationToken));
            month = month.AddMonths(1);
        }
        string? selectedSymbol = null;
        DateOnly selectedExpiry = default;
        foreach (var expiry in expiries.Distinct().Where(value =>
                     value >= request.Date && value <= last).OrderBy(value => value).Take(20))
        {
            var contracts = await groww.GetHistoricalContractsAsync(exchange,
                underlying, expiry, cancellationToken);
            selectedSymbol = HistoricalFutureUniverse.SelectForExpiry(contracts,
                exchange, underlying, expiry);
            if (selectedSymbol is null) continue;
            selectedExpiry = expiry;
            break;
        }
        if (selectedSymbol is null)
            return Conflict("Groww did not list a verified front future within 60 days of this session.");
        var start = new DateTimeOffset(request.Date.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var end = new DateTimeOffset(request.Date.ToDateTime(new TimeOnly(15, 30)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var history = await groww.GetHistoricalCandlesAsync(new(exchange, "FNO",
            selectedSymbol, start, end, "1minute"), cancellationToken);
        var instrument = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(value =>
            value.Exchange == exchange &&
            value.Segment == InstrumentSegment.FuturesAndOptions &&
            value.Type == InstrumentType.Future &&
            value.GrowwSymbol == selectedSymbol && value.ExpiryDate == selectedExpiry,
            cancellationToken);
        var mapped = instrument is not null;
        var added = await researchImporter.ImportAsync(exchange, underlying,
                selectedSymbol, selectedExpiry, history, start, end,
                clock.GetUtcNow(), cancellationToken);
        return Ok(new HistoricalFutureBackfillResult(request.Date, market,
            selectedExpiry, selectedSymbol, mapped, added));
    }

    [HttpGet]
    public async Task<ActionResult<HistoricalReplayResearchReport>> Get(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to,
        [FromQuery] string market = "nifty", [FromQuery] decimal stopPercent = 0.10m,
        [FromQuery] decimal targetPercent = 0.15m,
        [FromQuery] decimal entrySlippagePercent = 0.0025m,
        [FromQuery] decimal exitSlippagePercent = 0.0025m,
        [FromQuery] int maximumHoldMinutes = 45,
        [FromQuery] int minimumMinutesBetweenEntries = 5,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        if (from == default || to == default || from > to || !HistoricalReplaySessionCalendar.IsCompletedDate(to, clock.GetUtcNow()) ||
            to.DayNumber - from.DayNumber > 30)
            return BadRequest("Select a completed date range of up to 31 days.");
        market = market.Trim().ToLowerInvariant();
        if (market is not ("nifty" or "sensex" or "bank-nifty"))
            return BadRequest("Select Nifty, Sensex, or Bank Nifty.");
        var parameters = new HistoricalReplayParameters(stopPercent, targetPercent,
            entrySlippagePercent, exitSlippagePercent, maximumHoldMinutes,
            minimumMinutesBetweenEntries);
        try { parameters.Validate(); }
        catch (ArgumentOutOfRangeException) { return BadRequest("Invalid replay parameters."); }
        return Ok(await reader.ReadAsync(from, to, market, parameters, cancellationToken));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("backfill-recorded-candidates")]
    public async Task<ActionResult<HistoricalOptionBackfillResult>> BackfillRecordedCandidates(
        HistoricalOptionBackfillRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var indiaNow = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
        var today = DateOnly.FromDateTime(indiaNow.DateTime);
        var market = request.Market?.Trim().ToLowerInvariant() ?? string.Empty;
        var (exchange, underlying) = market switch
        {
            "nifty" => ("NSE", "NIFTY"),
            "sensex" => ("BSE", "SENSEX"),
            "bank-nifty" => ("NSE", "BANKNIFTY"),
            _ => (string.Empty, string.Empty)
        };
        if (underlying.Length == 0 || request.Date >= today || request.Date.Year < 2020 ||
            request.MaximumContracts is < 1 or > 20)
            return BadRequest("Choose a completed session, supported market and 1-20 contracts.");
        var localTime = TimeOnly.FromDateTime(indiaNow.DateTime);
        if (indiaNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
            localTime >= new TimeOnly(9, 0) && localTime <= new TimeOnly(15, 45))
            return Conflict("Research backfill is available outside market hours only.");
        var indexIds = await db.Instruments.AsNoTracking()
            .Where(value => value.Exchange == exchange && value.TradingSymbol == underlying &&
                            value.Segment == InstrumentSegment.Cash &&
                            value.Type == InstrumentType.Index)
            .Select(value => value.Id).ToArrayAsync(cancellationToken);
        var start = new DateTimeOffset(request.Date.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var end = new DateTimeOffset(request.Date.ToDateTime(new TimeOnly(15, 30)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var evaluatedSymbols = await db.StrategyEvaluations.AsNoTracking()
            .Where(value => indexIds.Contains(value.InstrumentId) &&
                            value.CandleTimeUtc >= start && value.CandleTimeUtc <= end &&
                            value.OptionSymbol != null)
            .Select(value => value.OptionSymbol!).Distinct()
            .ToArrayAsync(cancellationToken);
        var positionSymbols = await (from position in db.MarketPaperPositions.AsNoTracking()
            join instrument in db.Instruments.AsNoTracking() on position.ExecutionInstrumentId
                equals instrument.Id
            where position.Market == market && position.OpenedAtUtc >= start &&
                  position.OpenedAtUtc <= end
            select instrument.TradingSymbol).Distinct().ToArrayAsync(cancellationToken);
        var symbols = evaluatedSymbols.Concat(positionSymbols)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
            .Take(request.MaximumContracts).ToArray();
        var instruments = await db.Instruments.AsNoTracking()
            .Where(value => symbols.Contains(value.TradingSymbol) && value.Exchange == exchange &&
                            value.Segment == InstrumentSegment.FuturesAndOptions &&
                            (value.Type == InstrumentType.CallOption ||
                             value.Type == InstrumentType.PutOption))
            .ToListAsync(cancellationToken);
        var added = 0;
        var fetched = 0;
        var unavailable = new List<string>();
        foreach (var symbol in symbols)
        {
            var instrument = instruments.FirstOrDefault(value => value.TradingSymbol == symbol &&
                !string.IsNullOrWhiteSpace(value.GrowwSymbol));
            if (instrument is null) { unavailable.Add(symbol); continue; }
            var history = await groww.GetHistoricalCandlesAsync(new(exchange, "FNO",
                instrument.GrowwSymbol!, start, end, "1minute"), cancellationToken);
            added += await researchImporter.ImportMappedAsync(instrument, underlying, request.Date,
                history, start, end, clock.GetUtcNow(), cancellationToken);
            fetched++;
            // Keep research imports well below shared market-data rate limits.
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        return Ok(new HistoricalOptionBackfillResult(request.Date, market, symbols.Length,
            fetched, added, unavailable));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("backfill-universe-page")]
    public async Task<ActionResult<HistoricalUniverseBackfillResult>> BackfillUniversePage(
        HistoricalUniverseBackfillRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var indiaNow = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
        var today = DateOnly.FromDateTime(indiaNow.DateTime);
        var market = request.Market?.Trim().ToLowerInvariant() ?? string.Empty;
        var (exchange, underlying, padding) = market switch
        {
            "nifty" => ("NSE", "NIFTY", 500m),
            "sensex" => ("BSE", "SENSEX", 1500m),
            "bank-nifty" => ("NSE", "BANKNIFTY", 1000m),
            _ => (string.Empty, string.Empty, 0m)
        };
        if (underlying.Length == 0 || request.Date >= today || request.Date.Year < 2020 ||
            request.ExpiryDate < request.Date ||
            request.ExpiryDate > request.Date.AddDays(60) || request.Page < 0 ||
            request.PageSize is < 1 or > 20)
            return BadRequest("Choose a completed session, valid expiry and page size of 1-20.");
        var localTime = TimeOnly.FromDateTime(indiaNow.DateTime);
        if (indiaNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
            localTime >= new TimeOnly(9, 0) && localTime <= new TimeOnly(15, 45))
            return Conflict("Research backfill is available outside market hours only.");

        var start = new DateTimeOffset(request.Date.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var end = new DateTimeOffset(request.Date.ToDateTime(new TimeOnly(15, 30)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var indexIds = await db.Instruments.AsNoTracking()
            .Where(value => value.Exchange == exchange && value.TradingSymbol == underlying &&
                            value.Segment == InstrumentSegment.Cash &&
                            value.Type == InstrumentType.Index)
            .Select(value => value.Id).ToArrayAsync(cancellationToken);
        var dayRange = await db.Candles.AsNoTracking()
            .Where(value => indexIds.Contains(value.InstrumentId) &&
                            value.IntervalSeconds == 60 && value.OpenTimeUtc >= start &&
                            value.OpenTimeUtc < end)
            .Select(value => new { value.OpenTimeUtc, value.Low, value.High })
            .ToListAsync(cancellationToken);
        var coverage = HistoricalSessionCoverageEvaluator.Evaluate(request.Date,
            dayRange.Select(value => value.OpenTimeUtc));
        if (dayRange.GroupBy(value => value.OpenTimeUtc).Any(group =>
                group.Count() > 1))
            return Conflict("Duplicate underlying index minutes prevent a reproducible historical contract universe.");
        if (!coverage.IsComplete)
            return Conflict($"A complete underlying index session is required before selecting historical strikes; {coverage.MissingMinutes} of {coverage.ExpectedMinutes} minutes are missing.");

        var expiries = await groww.GetHistoricalExpiriesAsync(exchange, underlying,
            request.ExpiryDate.Year, request.ExpiryDate.Month, cancellationToken);
        if (!expiries.Contains(request.ExpiryDate))
            return BadRequest("Groww does not list that historical expiry for this market.");
        var listed = await groww.GetHistoricalContractsAsync(exchange, underlying,
            request.ExpiryDate, cancellationToken);
        var selected = HistoricalOptionUniverse.Select(listed, exchange, underlying,
            request.ExpiryDate, dayRange.Min(value => value.Low),
            dayRange.Max(value => value.High), padding);
        var universeFingerprint = HistoricalUniverseFingerprint.Compute(
            request.Date, market, request.ExpiryDate, selected,
            dayRange.Select(value => new HistoricalUniverseIndexRangePoint(
                value.OpenTimeUtc, value.Low, value.High)).ToArray());
        var receipts = await db.HistoricalUniversePageReceipts.AsNoTracking()
            .Where(value => value.SessionDate == request.Date &&
                value.Market == market && value.ExpiryDate == request.ExpiryDate)
            .Select(value => new { value.Page, value.PageSize,
                value.UniverseFingerprint })
            .ToArrayAsync(cancellationToken);
        if (receipts.Any(value => value.PageSize != request.PageSize ||
                value.UniverseFingerprint != universeFingerprint))
            return Conflict("The archived universe pages were fetched against a different contract list, index range, or page size; review the data revision before continuing.");
        var offset = (long)request.Page * request.PageSize;
        if (offset >= selected.Count && (selected.Count > 0 || request.Page != 0))
            return BadRequest("Page is beyond the selected historical contract universe.");
        var page = selected.Skip((int)offset).Take(request.PageSize).ToArray();
        var instruments = await db.Instruments.AsNoTracking()
            .Where(value => page.Contains(value.GrowwSymbol!) &&
                            value.Exchange == exchange &&
                            value.Segment == InstrumentSegment.FuturesAndOptions &&
                            value.ExpiryDate == request.ExpiryDate &&
                            (value.Type == InstrumentType.CallOption ||
                             value.Type == InstrumentType.PutOption))
            .ToListAsync(cancellationToken);
        var found = instruments.ToDictionary(value => value.GrowwSymbol!, StringComparer.Ordinal);
        var unavailable = new List<string>();
        var imported = 0;
        var archivedWithoutMetadata = 0;
        var fetched = 0;
        foreach (var symbol in page)
        {
            var candles = await groww.GetHistoricalCandlesAsync(new(exchange, "FNO", symbol,
                start, end, "1minute"), cancellationToken);
            if (found.TryGetValue(symbol, out var instrument))
                imported += await researchImporter.ImportMappedAsync(instrument, underlying, request.Date,
                    candles, start, end, clock.GetUtcNow(), cancellationToken);
            else
            {
                unavailable.Add(symbol);
                archivedWithoutMetadata += await researchImporter.ImportAsync(exchange,
                    underlying, symbol, request.ExpiryDate, candles, start, end,
                    clock.GetUtcNow(), cancellationToken);
            }
            fetched++;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        if (!receipts.Any(value => value.Page == request.Page))
        {
            db.HistoricalUniversePageReceipts.Add(new HistoricalUniversePageReceipt(
                Guid.NewGuid(), request.Date, market, request.ExpiryDate,
                request.Page, request.PageSize, selected.Count,
                universeFingerprint, JsonSerializer.Serialize(page),
                clock.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
        }
        return Ok(new HistoricalUniverseBackfillResult(request.Date, market,
            request.ExpiryDate, selected.Count, request.Page, page.Length, fetched,
            imported, archivedWithoutMetadata, unavailable));
    }

    [HttpGet("expiries")]
    public async Task<ActionResult<HistoricalReplayExpiriesResult>> GetExpiries(
        [FromQuery] DateOnly date, [FromQuery] string market,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var indiaNow = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
        var today = DateOnly.FromDateTime(indiaNow.DateTime);
        var normalizedMarket = market?.Trim().ToLowerInvariant() ?? string.Empty;
        var (exchange, underlying) = normalizedMarket switch
        {
            "nifty" => ("NSE", "NIFTY"),
            "sensex" => ("BSE", "SENSEX"),
            "bank-nifty" => ("NSE", "BANKNIFTY"),
            _ => (string.Empty, string.Empty)
        };
        if (underlying.Length == 0 || date >= today || date.Year < 2020)
            return BadRequest("Choose a completed day and supported market.");
        var localTime = TimeOnly.FromDateTime(indiaNow.DateTime);
        if (indiaNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
            localTime >= new TimeOnly(9, 0) && localTime <= new TimeOnly(15, 45))
            return Conflict("Historical contract discovery is available outside market hours only.");
        var last = date.AddDays(60);
        var expiries = new List<DateOnly>();
        var month = new DateOnly(date.Year, date.Month, 1);
        while (month <= last)
        {
            expiries.AddRange(await groww.GetHistoricalExpiriesAsync(exchange, underlying,
                month.Year, month.Month, cancellationToken));
            month = month.AddMonths(1);
        }
        return Ok(new HistoricalReplayExpiriesResult(date, normalizedMarket,
            expiries.Where(value => value >= date && value <= last).Distinct()
                .OrderBy(value => value).ToArray()));
    }
}

public sealed record HistoricalOptionBackfillRequest(DateOnly Date, string Market,
    int MaximumContracts = 20);

public sealed record HistoricalIndexBackfillRequest(DateOnly Date, string Market);

public sealed record HistoricalIndexBackfillResult(DateOnly Date, string Market,
    int AddedCandles, int PresentMinutes, int ExpectedMinutes, int MissingMinutes);

public sealed record HistoricalFutureBackfillResult(DateOnly Date, string Market,
    DateOnly ExpiryDate, string GrowwSymbol, bool MatchedInstrumentMetadata,
    int AddedCandles);

public sealed record HistoricalOptionBackfillResult(DateOnly Date, string Market,
    int SelectedContracts, int FetchedContracts, int AddedCandles,
    IReadOnlyList<string> UnavailableSymbols);

public sealed record HistoricalUniverseBackfillRequest(DateOnly Date, string Market,
    DateOnly ExpiryDate, int Page = 0, int PageSize = 20);

public sealed record HistoricalUniverseBackfillResult(DateOnly Date, string Market,
    DateOnly ExpiryDate, int TotalContractsInCorridor, int Page, int SelectedOnPage,
    int FetchedContracts, int AddedCandles, int ArchivedUnmappedCandles,
    IReadOnlyList<string> MissingInstrumentMetadata);

public sealed record HistoricalReplayExpiriesResult(DateOnly Date, string Market,
    IReadOnlyList<DateOnly> Expiries);

public sealed record HistoricalReplayCohortRangeReport(DateOnly From, DateOnly To,
    IReadOnlyList<DateOnly> IncludedDates,
    IReadOnlyList<HistoricalReplayExcludedDate> ExcludedDates);
