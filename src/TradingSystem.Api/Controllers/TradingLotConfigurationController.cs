using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.Execution;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/trading-lots")]
public sealed class TradingLotConfigurationController(ITradingLotConfigurationService service)
    : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TradingLotConfiguration>> Get(CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(cancellationToken));

    [ValidateAntiForgeryToken]
    [HttpPut]
    public async Task<ActionResult<TradingLotConfiguration>> Put(UpdateTradingLotsRequest request,
        CancellationToken cancellationToken)
    {
        var saved = await service.GetAsync(cancellationToken);
        return Ok(await service.SetAsync(request.NiftyLots, request.SensexLots, request.BankNiftyLots,
            saved.BankexLots, User.Identity?.Name ?? "administrator", cancellationToken));
    }
}

public sealed class UpdateTradingLotsRequest
{
    [Range(TradingLotConfiguration.MinimumLots, TradingLotConfiguration.MaximumLots)]
    public int NiftyLots { get; init; }

    [Range(TradingLotConfiguration.MinimumLots, TradingLotConfiguration.MaximumLots)]
    public int SensexLots { get; init; }

    [Range(TradingLotConfiguration.MinimumLots, TradingLotConfiguration.MaximumLots)]
    public int BankNiftyLots { get; init; }


}
