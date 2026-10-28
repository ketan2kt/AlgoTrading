using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TradingSystem.Application.Broker;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed partial class EfPreSessionBriefReader(TradingDbContext db, TimeProvider clock,
    IGrowwReadOnlyGateway groww, IHttpClientFactory httpClientFactory,
    UpstoxPreSessionSource upstox,
    ILogger<EfPreSessionBriefReader> logger)
    : IPreSessionBriefReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const string OfficialFeedsClientName = "PreSessionOfficialFeeds";
    private const string EcbDailyReferenceUrl =
        "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    private const string FedMonetaryRssUrl =
        "https://www.federalreserve.gov/feeds/press_monetary.xml";
    private static readonly (string Code, string Exchange, string Symbol)[] Markets =
    [
        ("nifty", "NSE", "NIFTY"),
        ("sensex", "BSE", "SENSEX")
    ];
    internal static readonly (string Stage, TimeOnly At)[] Stages =
    [
        ("Overnight", new TimeOnly(8, 45)),
        ("PreOpen", new TimeOnly(9, 12)),
        ("Outcome", new TimeOnly(15, 45))
    ];

    public async Task<PreSessionBriefReport> ReadAsync(DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        var stored = await db.PreSessionBriefSnapshots.AsNoTracking()
            .Where(value => value.SessionDate == sessionDate)
            .OrderBy(value => value.CapturedAtUtc)
            .ToListAsync(cancellationToken);
        var briefs = stored.Where(value => value.Stage != "Outcome").Select(value =>
            JsonSerializer.Deserialize<PreSessionMarketBrief>(value.PayloadJson, JsonOptions))
            .OfType<PreSessionMarketBrief>().ToArray();
        var outcomes = stored.Where(value => value.Stage == "Outcome").Select(value =>
            JsonSerializer.Deserialize<PreSessionObservedOutcome>(value.PayloadJson, JsonOptions))
            .OfType<PreSessionObservedOutcome>().ToArray();
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(5.5)).Date);
        var missing = (from market in Markets
                       from stage in Stages
                       where sessionDate < today ||
                             (sessionDate == today && now >= ToUtc(sessionDate,
                                 stage.At.AddMinutes(stage.Stage == "Outcome" ? 45 : 3)))
                       where !stored.Any(value => value.Market == market.Code && value.Stage == stage.Stage)
                       select $"{market.Code}: {stage.Stage} snapshot not captured").ToArray();
        var recent = await db.PreSessionBriefSnapshots.AsNoTracking()
            .Where(value => value.Stage == "Outcome" && value.SessionDate >= sessionDate.AddDays(-60) &&
                            value.SessionDate <= sessionDate)
            .Select(value => value.PayloadJson)
            .ToListAsync(cancellationToken);
        var reviewed = recent.Select(value =>
            JsonSerializer.Deserialize<PreSessionObservedOutcome>(value, JsonOptions))
            .OfType<PreSessionObservedOutcome>().ToArray();
        var scorecards = Markets.Select(market =>
        {
            var marketOutcomes = reviewed.Where(value => value.Market == market.Code).ToArray();
            var correct = marketOutcomes.Count(value => value.Assessment == "Correct");
            var incorrect = marketOutcomes.Count(value => value.Assessment == "Incorrect");
            var scored = correct + incorrect;
            return new PreSessionScorecard(market.Code, marketOutcomes.Length, scored,
                correct, incorrect, scored == 0 ? null : Math.Round(100m * correct / scored, 1));
        }).ToArray();
        return new PreSessionBriefReport(sessionDate, now, briefs, missing, outcomes, scorecards);
    }

    internal async Task CaptureAsync(DateOnly sessionDate, string stage,
        DateTimeOffset capturedAtUtc, CancellationToken cancellationToken)
    {
        if (!Stages.Any(value => value.Stage == stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        if (await db.PreSessionBriefSnapshots.AsNoTracking().CountAsync(value =>
                value.SessionDate == sessionDate && value.Stage == stage &&
                (value.Market == "nifty" || value.Market == "sensex"), cancellationToken) == Markets.Length)
            return;
        IReadOnlyList<PreSessionEvidence> externalEvidence = [];
        if (stage != "Outcome")
        {
            var upstoxTask = upstox.ReadAsync(sessionDate, capturedAtUtc, cancellationToken);
            var calendarTask = ReadFinanceCalendarAsync(sessionDate, capturedAtUtc, cancellationToken);
            var collected = (await upstoxTask).ToList();
            if (await calendarTask is { } calendar) collected.Add(calendar);
            externalEvidence = collected;
        }
        foreach (var market in Markets)
        {
            if (await db.PreSessionBriefSnapshots.AnyAsync(value => value.SessionDate == sessionDate &&
                    value.Market == market.Code && value.Stage == stage, cancellationToken)) continue;
            string payload;
            if (stage == "Outcome")
            {
                var morningRows = await db.PreSessionBriefSnapshots.AsNoTracking()
                    .Where(value => value.SessionDate == sessionDate && value.Market == market.Code &&
                                    value.Stage != "Outcome")
                    .OrderByDescending(value => value.CapturedAtUtc)
                    .ToListAsync(cancellationToken);
                var morning = morningRows.Select(value =>
                    JsonSerializer.Deserialize<PreSessionMarketBrief>(value.PayloadJson, JsonOptions))
                    .FirstOrDefault(value => value is not null);
                var outcome = await ReadOutcomeAsync(market, sessionDate, morning,
                    capturedAtUtc, cancellationToken);
                if (outcome is null) continue;
                payload = JsonSerializer.Serialize(outcome, JsonOptions);
            }
            else
            {
                var source = await ReadSourceDataAsync(market, sessionDate, stage, capturedAtUtc,
                    externalEvidence, cancellationToken);
                payload = JsonSerializer.Serialize(PreSessionBriefBuilder.Build(source), JsonOptions);
            }
            var snapshot = new PreSessionBriefSnapshot(Guid.NewGuid(), market.Code, sessionDate,
                stage, capturedAtUtc, payload);
            db.PreSessionBriefSnapshots.Add(snapshot);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                       { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.Entry(snapshot).State = EntityState.Detached;
            }
        }
    }

    private async Task<PreSessionSourceData> ReadSourceDataAsync(
        (string Code, string Exchange, string Symbol) market, DateOnly sessionDate,
        string stage, DateTimeOffset capturedAtUtc,
        IReadOnlyList<PreSessionEvidence> externalEvidence, CancellationToken cancellationToken)
    {
        var startUtc = ToUtc(sessionDate, TimeOnly.MinValue);
        var instrumentId = await db.Instruments.AsNoTracking()
            .Where(value => value.Exchange == market.Exchange && value.TradingSymbol == market.Symbol &&
                            value.Type == InstrumentType.Index && value.IsActive)
            .Select(value => (Guid?)value.Id).FirstOrDefaultAsync(cancellationToken);
        if (instrumentId is null)
            return new(market.Code, sessionDate, stage, capturedAtUtc,
                null, null, null, null, null, null, null, null, null, null, null,
                ExternalEvidence: externalEvidence);

        var candles = await db.Candles.AsNoTracking()
            .Where(value => value.InstrumentId == instrumentId.Value && value.Source == "Groww" &&
                            value.IntervalSeconds == 60 && value.OpenTimeUtc >= startUtc.AddDays(-10) &&
                            value.OpenTimeUtc < startUtc)
            .OrderByDescending(value => value.OpenTimeUtc).Take(5000)
            .Select(value => new { value.OpenTimeUtc, value.Open, value.High, value.Low, value.Close })
            .ToListAsync(cancellationToken);
        var sessions = candles
            .Where(value => IsRegularSession(value.OpenTimeUtc))
            .GroupBy(value => DateOnly.FromDateTime(value.OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).Date))
            .OrderByDescending(group => group.Key).ToArray();
        var previous = sessions.FirstOrDefault();
        var recentSessions = sessions.Where(group => group.Count() >= 180).Take(5)
            .Select(group => new PreSessionHistoricalSession(group.Key,
                group.OrderBy(value => value.OpenTimeUtc).First().Open,
                group.Max(value => value.High), group.Min(value => value.Low),
                group.OrderByDescending(value => value.OpenTimeUtc).First().Close,
                group.Count()))
            .OrderBy(value => value.Date).ToArray();
        var lastCandle = previous?.OrderByDescending(value => value.OpenTimeUtc).FirstOrDefault();
        var firstCandle = previous?.OrderBy(value => value.OpenTimeUtc).FirstOrDefault();

        OptionChainSnapshot? option = null;
        if (previous is not null)
        {
            var previousStartUtc = ToUtc(previous.Key, TimeOnly.MinValue);
            option = await db.OptionChainSnapshots.AsNoTracking()
                .Where(value => value.UnderlyingInstrumentId == instrumentId.Value &&
                                value.ExpiryDate >= sessionDate &&
                                value.SourceTimestampUtc >= previousStartUtc &&
                                value.SourceTimestampUtc < startUtc)
                .OrderByDescending(value => value.SourceTimestampUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }
        (decimal? call, decimal? put) = option is null
            ? (null, null) : ParseOiStrikes(option.PayloadJson);

        decimal? preOpenQuote = null;
        DateTimeOffset? preOpenQuoteAtUtc = null;
        if (stage == "PreOpen")
        {
            try
            {
                using var quoteTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                quoteTimeout.CancelAfter(TimeSpan.FromSeconds(6));
                var quote = await groww.GetQuoteAsync(
                    new GrowwQuoteRequest(market.Exchange, "CASH", market.Symbol), quoteTimeout.Token);
                if (quote.LastPrice > 0 && quote.LastTradeTimeEpochMilliseconds is > 0)
                {
                    var quoteAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                        quote.LastTradeTimeEpochMilliseconds.Value);
                    if (quoteAtUtc >= ToUtc(sessionDate, new TimeOnly(9, 0)) &&
                        quoteAtUtc <= capturedAtUtc.AddMinutes(1))
                    {
                        preOpenQuote = quote.LastPrice;
                        preOpenQuoteAtUtc = quoteAtUtc;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                LogGrowwPreOpenQuoteUnavailable(logger, market.Code, exception);
            }
        }

        var usdInrReference = await ReadUsdInrReferenceAsync(sessionDate, cancellationToken);
        var usTenYearYield = await ReadUsTenYearYieldAsync(sessionDate, cancellationToken);
        var recentFedRelease = await ReadFedReleaseAsync(capturedAtUtc, cancellationToken);

        // Existing Groww index/option polling starts at 9:15. Never relabel a previous
        // close as today's indicative auction price; a licensed pre-open feed is needed.
        return new(market.Code, sessionDate, stage, capturedAtUtc,
            firstCandle?.Open, lastCandle?.Close,
            previous?.Max(value => value.High), previous?.Min(value => value.Low),
            lastCandle?.OpenTimeUtc, null, null, call, put, option?.ExpiryDate,
            option?.SourceTimestampUtc, recentSessions, preOpenQuote, preOpenQuoteAtUtc,
            usdInrReference, usTenYearYield, recentFedRelease, externalEvidence);
    }

    private async Task<FedMonetaryRelease?> ReadFedReleaseAsync(
        DateTimeOffset capturedAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var response = await httpClientFactory.CreateClient(OfficialFeedsClientName).GetAsync(
                FedMonetaryRssUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 200_000) return null;
            var xml = await response.Content.ReadAsStringAsync(timeout.Token);
            return FedMonetaryNewsParser.ParseLatest(xml, capturedAtUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            LogFedReleaseUnavailable(logger, exception);
            return null;
        }
    }

    private async Task<PreSessionEvidence?> ReadFinanceCalendarAsync(DateOnly sessionDate,
        DateTimeOffset capturedAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var from = sessionDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var to = sessionDate.AddDays(1).ToString("yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture);
            var url = "https://www.financecalendar.com/wp-json/fc/v1/calendar?" +
                $"from={from}&to={to}&impact=high&limit=100";
            using var response = await httpClientFactory.CreateClient(OfficialFeedsClientName).GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 500_000) return null;
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            if (json.Length > 500_000) return null;
            return FinanceCalendarParser.Parse(json, capturedAtUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            LogFinanceCalendarUnavailable(logger, exception);
            return null;
        }
    }

    private async Task<TreasuryTenYearYield?> ReadUsTenYearYieldAsync(
        DateOnly sessionDate, CancellationToken cancellationToken)
    {
        foreach (var month in new[] { sessionDate, sessionDate.AddMonths(-1) })
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var yearMonth = month.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
                var url = "https://home.treasury.gov/resource-center/data-chart-center/interest-rates/" +
                    $"daily-treasury-rates.csv/all/{yearMonth}?_format=csv&" +
                    $"field_tdr_date_value_month={yearMonth}&page=&type=daily_treasury_yield_curve";
                using var response = await httpClientFactory.CreateClient(OfficialFeedsClientName).GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 100_000) continue;
                var csv = await response.Content.ReadAsStringAsync(timeout.Token);
                var yield = TreasuryYieldParser.ParseLatestTenYear(csv, sessionDate);
                if (yield is not null && yield.Date >= sessionDate.AddDays(-5)) return yield;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                LogUsTreasuryUnavailable(logger, exception);
            }
        }
        return null;
    }

    private async Task<EcbUsdInrReference?> ReadUsdInrReferenceAsync(
        DateOnly sessionDate, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var response = await httpClientFactory.CreateClient(OfficialFeedsClientName).GetAsync(
                EcbDailyReferenceUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 100_000) return null;
            var xml = await response.Content.ReadAsStringAsync(timeout.Token);
            var reference = EcbReferenceRateParser.ParseUsdInr(xml);
            return reference is not null && reference.Date <= sessionDate &&
                reference.Date >= sessionDate.AddDays(-5) ? reference : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            LogEcbReferenceUnavailable(logger, exception);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Groww pre-open index quote unavailable for {Market}")]
    private static partial void LogGrowwPreOpenQuoteUnavailable(
        ILogger logger, string market, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "ECB reference rates unavailable for pre-session brief")]
    private static partial void LogEcbReferenceUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "US Treasury yield unavailable for pre-session brief")]
    private static partial void LogUsTreasuryUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Federal Reserve monetary release feed unavailable for pre-session brief")]
    private static partial void LogFedReleaseUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Finance Calendar schedule unavailable for pre-session brief")]
    private static partial void LogFinanceCalendarUnavailable(ILogger logger, Exception exception);

    private async Task<PreSessionObservedOutcome?> ReadOutcomeAsync(
        (string Code, string Exchange, string Symbol) market, DateOnly sessionDate,
        PreSessionMarketBrief? brief, DateTimeOffset assessedAtUtc,
        CancellationToken cancellationToken)
    {
        var instrumentId = await db.Instruments.AsNoTracking()
            .Where(value => value.Exchange == market.Exchange && value.TradingSymbol == market.Symbol &&
                            value.Type == InstrumentType.Index && value.IsActive)
            .Select(value => (Guid?)value.Id).FirstOrDefaultAsync(cancellationToken);
        if (instrumentId is null) return null;
        var start = ToUtc(sessionDate, new TimeOnly(9, 15));
        var end = ToUtc(sessionDate, new TimeOnly(15, 31));
        var candles = await db.Candles.AsNoTracking()
            .Where(value => value.InstrumentId == instrumentId.Value && value.Source == "Groww" &&
                            value.IntervalSeconds == 60 && value.OpenTimeUtc >= start && value.OpenTimeUtc < end)
            .OrderBy(value => value.OpenTimeUtc)
            .Select(value => new { value.OpenTimeUtc, value.Open, value.High, value.Low, value.Close })
            .ToListAsync(cancellationToken);
        if (candles.Count == 0) return null;
        var high = candles.Max(value => value.High);
        var low = candles.Min(value => value.Low);
        return PreSessionOutcomePolicy.Assess(market.Code, sessionDate,
            candles[0].Open, high, low, candles[^1].Close,
            candles.Count, candles[0].OpenTimeUtc, candles[^1].OpenTimeUtc,
            assessedAtUtc, brief);
    }

    private static (decimal? Call, decimal? Put) ParseOiStrikes(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("selectedStrikes", out var strikes) ||
                strikes.ValueKind != JsonValueKind.Array) return (null, null);
            decimal callOi = 0, putOi = 0;
            decimal? callStrike = null, putStrike = null;
            foreach (var strike in strikes.EnumerateArray())
            {
                if (!strike.TryGetProperty("strikePrice", out var price) ||
                    !price.TryGetDecimal(out var strikePrice)) continue;
                if (TryReadOi(strike, "call", out var nextCall) && nextCall > callOi)
                {
                    callOi = nextCall;
                    callStrike = strikePrice;
                }
                if (TryReadOi(strike, "put", out var nextPut) && nextPut > putOi)
                {
                    putOi = nextPut;
                    putStrike = strikePrice;
                }
            }
            return (callStrike, putStrike);
        }
        catch (JsonException) { return (null, null); }
    }

    private static bool TryReadOi(JsonElement strike, string side, out decimal oi)
    {
        oi = 0;
        return strike.TryGetProperty(side, out var contract) &&
               contract.ValueKind == JsonValueKind.Object &&
               contract.TryGetProperty("openInterest", out var value) &&
               value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out oi);
    }

    private static bool IsRegularSession(DateTimeOffset utc)
    {
        var local = utc.ToOffset(TimeSpan.FromHours(5.5));
        var time = TimeOnly.FromDateTime(local.DateTime);
        return local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
               time >= new TimeOnly(9, 15) && time < new TimeOnly(15, 31);
    }

    internal static DateTimeOffset ToUtc(DateOnly date, TimeOnly time) =>
        new DateTimeOffset(date.ToDateTime(time), TimeSpan.FromHours(5.5)).ToUniversalTime();
}
