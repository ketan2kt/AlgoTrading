using TradingSystem.Infrastructure;
using TradingSystem.Application.Broker;
using TradingSystem.Domain;

namespace TradingSystem.ContractTests;

public sealed class FoundationCapabilityTests
{
    [Fact]
    public void GrowwGatewayIsAbsentFromFoundation()
    {
        var implementationTypes = typeof(DependencyInjection).Assembly.GetTypes();

        Assert.DoesNotContain(
            implementationTypes,
            type => type.Name.Contains("GrowwBrokerGateway", StringComparison.Ordinal));
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
