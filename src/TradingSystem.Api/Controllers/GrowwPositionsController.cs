using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.Broker;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/broker/groww/positions")]
public sealed class GrowwPositionsController(IGrowwReadOnlyGateway gateway) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GrowwPositionsResponse>> Get(CancellationToken cancellationToken)
    {
        var cash = await gateway.GetPositionsAsync("CASH", cancellationToken);
        var fno = await gateway.GetPositionsAsync("FNO", cancellationToken);
        var rows = new List<GrowwLivePosition>();
        foreach (var position in cash.Concat(fno).Where(value => value.Quantity != 0))
        {
            decimal? current = null;
            try
            {
                current = (await gateway.GetQuoteAsync(new GrowwQuoteRequest(
                    position.Exchange, position.Segment, position.TradingSymbol), cancellationToken)).LastPrice;
            }
            catch (GrowwApiException)
            {
                // Position remains visible even when its optional live quote is unavailable.
            }
            var unrealised = current is { } price
                ? (price - position.NetPrice) * position.Quantity
                : (decimal?)null;
            rows.Add(new GrowwLivePosition(position.TradingSymbol, position.Segment,
                position.Exchange, position.Product, position.Quantity, position.NetPrice,
                current, position.RealisedPnl, unrealised));
        }
        return Ok(new GrowwPositionsResponse(rows, DateTimeOffset.UtcNow, "ReadOnly"));
    }
}

public sealed record GrowwPositionsResponse(
    IReadOnlyList<GrowwLivePosition> Positions,
    DateTimeOffset ObservedAtUtc,
    string Capability);

public sealed record GrowwLivePosition(string TradingSymbol, string Segment, string Exchange,
    string Product, int Quantity, decimal AveragePrice, decimal? CurrentPrice,
    decimal RealisedPnl, decimal? UnrealisedPnl);
