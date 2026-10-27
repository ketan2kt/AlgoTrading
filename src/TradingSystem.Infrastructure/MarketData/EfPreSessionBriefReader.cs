using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed class EfPreSessionBriefReader(TradingDbContext db, TimeProvider clock)
    : IPreSessionBriefReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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
                var source = await ReadSourceDataAsync(market, sessionDate, stage, capturedAtUtc, cancellationToken);
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
        string stage, DateTimeOffset capturedAtUtc, CancellationToken cancellationToken)
    {
        var startUtc = ToUtc(sessionDate, TimeOnly.MinValue);
        var instrumentId = await db.Instruments.AsNoTracking()
            .Where(value => value.Exchange == market.Exchange && value.TradingSymbol == market.Symbol &&
                            value.Type == InstrumentType.Index && value.IsActive)
            .Select(value => (Guid?)value.Id).FirstOrDefaultAsync(cancellationToken);
        if (instrumentId is null)
            return new(market.Code, sessionDate, stage, capturedAtUtc,
                null, null, null, null, null, null, null, null, null, null, null);

        var candles = await db.Candles.AsNoTracking()
            .Where(value => value.InstrumentId == instrumentId.Value && value.Source == "Groww" &&
                            value.IntervalSeconds == 60 && value.OpenTimeUtc >= startUtc.AddDays(-10) &&
                            value.OpenTimeUtc < startUtc)
            .OrderByDescending(value => value.OpenTimeUtc).Take(5000)
            .Select(value => new { value.OpenTimeUtc, value.Open, value.High, value.Low, value.Close })
            .ToListAsync(cancellationToken);
        var previous = candles
            .Where(value => IsRegularSession(value.OpenTimeUtc))
            .GroupBy(value => DateOnly.FromDateTime(value.OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).Date))
            .OrderByDescending(group => group.Key).FirstOrDefault();
        var lastCandle = previous?.OrderByDescending(value => value.OpenTimeUtc).FirstOrDefault();
        var firstCandle = previous?.OrderBy(value => value.OpenTimeUtc).FirstOrDefault();

        OptionChainSnapshot? option = null;
        if (previous is not null)
        {
            var previousStartUtc = ToUtc(previous.Key, TimeOnly.MinValue);
            option = await db.OptionChainSnapshots.AsNoTracking()
                .Where(value => value.UnderlyingInstrumentId == instrumentId.Value &&
                                value.SourceTimestampUtc >= previousStartUtc &&
                                value.SourceTimestampUtc < startUtc)
                .OrderByDescending(value => value.SourceTimestampUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }
        (decimal? call, decimal? put) = option is null
            ? (null, null) : ParseOiStrikes(option.PayloadJson);

        // Existing Groww index/option polling starts at 9:15. Never relabel a previous
        // close as today's indicative auction price; a licensed pre-open feed is needed.
        return new(market.Code, sessionDate, stage, capturedAtUtc,
            firstCandle?.Open, lastCandle?.Close,
            previous?.Max(value => value.High), previous?.Min(value => value.Low),
            lastCandle?.OpenTimeUtc, null, null, call, put, option?.ExpiryDate,
            option?.SourceTimestampUtc);
    }

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
