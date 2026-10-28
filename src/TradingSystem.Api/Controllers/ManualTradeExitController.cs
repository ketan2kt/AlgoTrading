using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/paper/trades")]
public sealed class ManualTradeExitController(TradingDbContext db, TimeProvider clock) : ControllerBase
{
    [ValidateAntiForgeryToken]
    [HttpPost("{market}/{tradeId:guid}/exit")]
    public async Task<IActionResult> RequestExit(string market, Guid tradeId,
        CancellationToken cancellationToken)
    {
        if (market is not ("nifty" or "sensex")) return NotFound();
        if (tradeId == Guid.Empty) return BadRequest();
        var position = await db.MarketPaperPositions.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == tradeId && value.Market == market, cancellationToken);
        Guid underlyingInstrumentId;
        if (position is not null)
        {
            if (position.Status != "Active") return Conflict("The trade is already closed.");
            underlyingInstrumentId = position.UnderlyingInstrumentId;
        }
        else
        {
            if (market != "nifty") return NotFound();
            var signal = await db.Signals.AsNoTracking().SingleOrDefaultAsync(
                value => value.Id == tradeId, cancellationToken);
            if (signal is null) return NotFound();
            var entryReference = $"{tradeId:N}-ENTRY";
            var filled = await db.PaperBrokerEvents.AsNoTracking().AnyAsync(value =>
                value.ClientReference == entryReference && value.EventType == "OrderFilled",
                cancellationToken);
            if (!filled) return Conflict("The paper entry has not filled.");
            if (await db.PaperTradeResults.AsNoTracking().AnyAsync(value =>
                    value.SignalId == tradeId, cancellationToken))
                return Conflict("The trade is already closed.");
            underlyingInstrumentId = signal.InstrumentId;
        }

        if (!await ManualTradeExitRequest.ExistsAsync(db, market, tradeId, cancellationToken))
        {
            var now = clock.GetUtcNow();
            var details = JsonSerializer.Serialize(new { tradeId, market,
                requestedBy = User.Identity?.Name ?? "administrator", requestedAtUtc = now });
            db.ApplicationSettings.Add(new ApplicationSetting(Guid.NewGuid(), TradingMode.Paper,
                ManualTradeExitRequest.Key(market, tradeId), details, now));
            db.MarketStrategyAudits.Add(new MarketStrategyAudit(Guid.NewGuid(), market,
                underlyingInstrumentId, now, ManualTradeExitRequest.Outcome(tradeId), 0m,
                details));
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException)
            {
                if (!await ManualTradeExitRequest.ExistsAsync(db, market, tradeId, cancellationToken))
                    throw;
                // Another administrator submitted the same request concurrently.
            }
        }
        return Accepted(new { tradeId, status = "Exit requested" });
    }
}
