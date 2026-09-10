using Microsoft.AspNetCore.Mvc;
using TradingSystem.Api.Controllers;
using TradingSystem.Application.Execution;

namespace TradingSystem.IntegrationTests;

public sealed class HeroZeroEndpointTests
{
    [Fact]
    public void NeutralRouteAndLegacyRouteAreBothAvailable()
    {
        var routes = typeof(HeroZeroController).GetCustomAttributes(typeof(RouteAttribute), true)
            .Cast<RouteAttribute>().Select(x => x.Template).ToArray();
        Assert.Contains("api/expiry-monitor", routes);
        Assert.Contains("api/hero-zero", routes);
        Assert.Contains("api/market-monitor", routes);
    }

    [Fact]
    public void ControllerReturnsMonitorSnapshot()
    {
        var snapshot = new HeroZeroMonitorSnapshot("nifty", false, null, "Watching next expiry",
            "No entry today.", DateTimeOffset.UnixEpoch, null, null, null, [], 0, 0, 0);
        var result = new HeroZeroController(new Stub(snapshot)).Get("nifty");
        Assert.Equal(snapshot, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private sealed class Stub(HeroZeroMonitorSnapshot snapshot) : IHeroZeroMonitorReader
    {
        public HeroZeroMonitorSnapshot GetSnapshot(string market) => snapshot;
    }
}
