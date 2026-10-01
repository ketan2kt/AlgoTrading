using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/market-analysis/backfill")]
public sealed class ResearchBackfillController(IGrowwReadOnlyGateway groww,
    TradingDbContext db) : ControllerBase
{
    [ValidateAntiForgeryToken]
    [HttpPost]
    public async Task<ActionResult<ResearchBackfillResponse>> Backfill(
        ResearchBackfillRequest request, CancellationToken cancellationToken)
    {
        var symbol = request.Market.Trim().ToLowerInvariant() switch
        {
            "nifty" => "NIFTY",
            "sensex" => "SENSEX",
            _ => null
        };
        if (symbol is null) return BadRequest("Market must be nifty or sensex.");
        if (request.From >= request.To || request.To.DayNumber - request.From.DayNumber > 31)
            return BadRequest("Backfill range must be between one and 31 calendar days.");
        var instrument = await db.Instruments.SingleOrDefaultAsync(value =>
            value.TradingSymbol == symbol && value.Type == InstrumentType.Index && value.IsActive,
            cancellationToken);
        if (instrument?.GrowwSymbol is null)
            return Conflict("Synchronise the official Groww instrument master first.");
        var india = FindIndiaTimeZone();
        DateTimeOffset Utc(DateOnly date, TimeOnly time)
        {
            var local = date.ToDateTime(time);
            return new DateTimeOffset(local, india.GetUtcOffset(local)).ToUniversalTime();
        }
        var start = Utc(request.From, new TimeOnly(9, 15));
        var end = Utc(request.To.AddDays(1), new TimeOnly(0, 0));
        var payload = await groww.GetHistoricalCandlesAsync(new(instrument.Exchange, "CASH",
            instrument.GrowwSymbol, start, end, "1minute"), cancellationToken);
        var observed = payload.Candles.Select(value => new
        {
            Candle = value,
            Time = DateTimeOffset.Parse(value.SourceTimestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
        }).Where(value => value.Time >= start && value.Time < end).ToArray();
        var times = observed.Select(value => value.Time).ToArray();
        var existing = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == instrument.Id && value.IntervalSeconds == 60 &&
                times.Contains(value.OpenTimeUtc))
            .Select(value => value.OpenTimeUtc).ToHashSetAsync(cancellationToken);
        var added = 0;
        foreach (var value in observed.Where(value => !existing.Contains(value.Time)))
        {
            db.Candles.Add(new Candle(Guid.NewGuid(), instrument.Id, value.Time, 60,
                value.Candle.Open, value.Candle.High, value.Candle.Low, value.Candle.Close,
                value.Candle.Volume, "Groww", value.Candle.OpenInterest));
            added++;
        }
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new ResearchBackfillResponse(symbol, request.From, request.To,
            payload.Candles.Count, added, payload.Candles.Count - added));
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        throw new InvalidOperationException("India time zone is unavailable.");
    }
}

public sealed record ResearchBackfillRequest(string Market, DateOnly From, DateOnly To);
public sealed record ResearchBackfillResponse(string Market, DateOnly From, DateOnly To,
    int Received, int Added, int Existing);
