using Microsoft.AspNetCore.Mvc;
using TradingSystem.Api.Controllers;

namespace TradingSystem.IntegrationTests;

public sealed class PaperReportsEndpointTests
{
    [Fact]
    public void NeutralAndLegacyRoutesAreBothAvailable()
    {
        var routes = typeof(PaperReportsController)
            .GetCustomAttributes(typeof(RouteAttribute), true)
            .Cast<RouteAttribute>().Select(x => x.Template).ToArray();

        Assert.Contains("api/market-analysis", routes);
        Assert.Contains("api/reports/paper-trading", routes);
    }
}
