using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.ContractTests;

public sealed class FailedSetupExitAuditTests
{
    [Fact]
    public void ExecutionAssemblyCannotReintroduceCaseOnlyAnonymousAuditFields()
    {
        var collisions = typeof(PositionManagementRecovery).Assembly.GetTypes()
            .Where(type => type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal))
            .SelectMany(type => type.GetProperties()
                .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => type.Name + ": " + string.Join(", ", group.Select(property => property.Name))))
            .ToArray();
        Assert.Empty(collisions);
    }

    [Theory]
    [InlineData(696.30, 57800, 750.25, 675.25)]
    [InlineData(797.65, 79800, 825.35, 742.80)]
    public async Task QualifiedExitAuditDoesNotThrowOrEnterStopOnlyRecovery(
        decimal optionPrice, decimal underlyingPrice, decimal entry, decimal stop)
    {
        var id = Guid.NewGuid();
        var decision = new FailedSetupExitDecision(true, .55m,
            ["Completed post-entry candles confirm failed setup."]);
        var normalCompleted = false;
        await PositionManagementRecovery.RunAsync(protectionOnly =>
        {
            Assert.False(protectionOnly);
            using var document = JsonDocument.Parse(FailedSetupExitAudit.Serialize(
                id, optionPrice, underlyingPrice, decision));
            Assert.Equal(optionPrice, document.RootElement.GetProperty("price").GetDecimal());
            Assert.Equal(underlyingPrice, document.RootElement.GetProperty("underlyingPrice").GetDecimal());
            Assert.Equal(id, document.RootElement.GetProperty("positionId").GetGuid());
            Assert.Equal(.55m, document.RootElement.GetProperty("AdverseR").GetDecimal());
            var exit = MarketOptionPositionPolicy.Decide(Direction.Buy, optionPrice,
                entry, stop, entry * 1.1m, entry - stop, 60, 20,
                ProfessionalExitPolicy.For(ProfessionalTradeRegime.Directional),
                false, false, decision.ShouldExit, false, false, true, false);
            Assert.Equal("FailedSetupExit", exit.ExitReason);
            Assert.Equal(0, exit.PartialQuantity);
            normalCompleted = true;
            return Task.CompletedTask;
        }, (_, _) => throw new InvalidOperationException("Exit audit must not require recovery."),
            CancellationToken.None);
        Assert.True(normalCompleted);
    }
}
