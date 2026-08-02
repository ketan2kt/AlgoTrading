using NetArchTest.Rules;
using TradingSystem.Application.SystemStatus;
using TradingSystem.Domain;

namespace TradingSystem.ArchitectureTests;

public sealed class DependencyRulesTests
{
    [Fact]
    public void DomainDoesNotDependOnOuterLayers()
    {
        var result = Types.InAssembly(typeof(TradingMode).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "TradingSystem.Application",
                "TradingSystem.Infrastructure",
                "TradingSystem.Api",
                "TradingSystem.Worker")
            .GetResult();

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void ApplicationDoesNotDependOnInfrastructureOrHosts()
    {
        var result = Types.InAssembly(typeof(ISystemStatusReader).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "TradingSystem.Infrastructure",
                "TradingSystem.Api",
                "TradingSystem.Worker")
            .GetResult();

        Assert.True(result.IsSuccessful);
    }
}
