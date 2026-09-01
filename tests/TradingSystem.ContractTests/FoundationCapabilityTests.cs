using TradingSystem.Infrastructure;
using TradingSystem.Application.Broker;
using TradingSystem.Domain;

namespace TradingSystem.ContractTests;

public sealed class FoundationCapabilityTests
{
    [Fact]
    public void GrowwGatewayIsExplicitAndSupportsBrokerSideProtection()
    {
        var implementationTypes = typeof(DependencyInjection).Assembly.GetTypes();

        var gateway = Assert.Single(implementationTypes,
            type => type.Name == "GrowwBrokerGateway");
        Assert.Contains(typeof(IBrokerGateway), gateway.GetInterfaces());
        Assert.Contains(typeof(ILiveBrokerProtectionGateway), gateway.GetInterfaces());
    }

    [Fact]
    public void BrokerContractIsModeAwareAndCancellationAware()
    {
        Assert.Equal(typeof(TradingMode), typeof(IBrokerGateway).GetProperty("Mode")!.PropertyType);
        Assert.NotNull(typeof(IBrokerGateway).GetMethod("SubmitAsync"));
        Assert.NotNull(typeof(IBrokerGateway).GetMethod("CancelAsync"));
        Assert.NotNull(typeof(IBrokerGateway).GetMethod("ReconcileAsync"));
    }
}
