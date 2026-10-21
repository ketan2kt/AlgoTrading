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
        CancellationToken cancellationToken) => Ok(await service.SetAsync(request.NiftyLots,
        request.SensexLots, User.Identity?.Name ?? "administrator", cancellationToken));
}

public sealed class UpdateTradingLotsRequest
{
    [Range(TradingLotConfiguration.MinimumLots, TradingLotConfiguration.MaximumLots)]
    public int NiftyLots { get; init; } = TradingLotConfiguration.DefaultLots;

    [Range(TradingLotConfiguration.MinimumLots, TradingLotConfiguration.MaximumLots)]
    public int SensexLots { get; init; } = TradingLotConfiguration.DefaultLots;
}
