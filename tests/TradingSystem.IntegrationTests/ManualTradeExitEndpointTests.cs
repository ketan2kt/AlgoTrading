using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Api.Controllers;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.IntegrationTests;

public sealed class ManualTradeExitEndpointTests
{
    [Fact]
    public void ExitEndpointRequiresAdministratorAndAntiforgery()
    {
        var controller = typeof(ManualTradeExitController);
        var authorization = Assert.Single(controller
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());
        Assert.Equal("Administrator", authorization.Roles);
        var endpoint = controller.GetMethod(nameof(ManualTradeExitController.RequestExit));
        Assert.NotNull(endpoint);
        Assert.Single(endpoint.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        Assert.Single(endpoint.GetCustomAttributes(typeof(HttpPostAttribute), true));
    }

    [Fact]
    public void RequestKeyIsStableForRetryAndDistinctPerTrade()
    {
        var first = Guid.NewGuid();
        Assert.Equal(ManualTradeExitRequest.Outcome(first), ManualTradeExitRequest.Outcome(first));
        Assert.NotEqual(ManualTradeExitRequest.Outcome(first), ManualTradeExitRequest.Outcome(Guid.NewGuid()));
        Assert.NotEqual(ManualTradeExitRequest.Key("nifty", first),
            ManualTradeExitRequest.Key("sensex", first));
    }
}
