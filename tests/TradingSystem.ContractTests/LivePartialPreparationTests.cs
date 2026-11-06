using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class LivePartialPreparationTests
{
    private static readonly string[] ExpectedPhases = ["PartialProtectionResizing", "PartialSubmitting"];
    [Fact]
    public async Task DurablePhasePrecedesResizeAndSellAuthorization()
    {
        var intent = Intent();
        var phases = new List<string>();
        var result = await AutomaticLiveExecutionService.PreparePartialProtectionAsync(intent, 50,
            () => { phases.Add(intent.Status); return Task.CompletedTask; },
            quantity =>
            {
                Assert.Equal("PartialProtectionResizing", Assert.Single(phases));
                Assert.Equal(50, quantity);
                return Task.FromResult(Stop(quantity));
            }, () => throw new InvalidOperationException("Restoration must not run on success."));
        Assert.NotNull(result);
        Assert.Equal(ExpectedPhases, phases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostResizeResponseOrPostResizeDatabaseFailureRestoresFullStop(bool databaseFailure)
    {
        var intent = Intent();
        var protectionQuantity = 100;
        var saves = 0;
        var result = await AutomaticLiveExecutionService.PreparePartialProtectionAsync(intent, 50,
            () => { if (++saves == 2 && databaseFailure) throw new IOException("Lost database acknowledgement"); return Task.CompletedTask; },
            quantity =>
            {
                protectionQuantity = quantity;
                if (!databaseFailure) throw new IOException("Broker accepted resize but response was lost");
                return Task.FromResult(Stop(quantity));
            }, () =>
            {
                Assert.Equal("PartialProtectionResizing", intent.Status);
                protectionQuantity = intent.FilledQuantity;
                intent.RetainProtectedRemainder("Verified restored stop");
                return Task.CompletedTask;
            });
        Assert.Null(result); // Caller MUST NOT submit the partial sell.
        Assert.Equal(100, protectionQuantity);
        Assert.Equal("ProtectedScaleMismatch", intent.Status);
        Assert.Equal(0, intent.MirroredPartialExitQuantity);
    }

    [Fact]
    public async Task RestoreFailureRetainsRecoverablePhaseRatherThanAbandoningProtection()
    {
        var intent = Intent();
        await Assert.ThrowsAsync<IOException>(() => AutomaticLiveExecutionService.PreparePartialProtectionAsync(intent, 50,
            () => Task.CompletedTask,
            _ => throw new IOException("Resize timeout"),
            () => throw new IOException("Temporary restore timeout")));
        Assert.Equal("PartialProtectionResizing", intent.Status);
    }

    [Fact]
    public async Task InitialDatabaseFailureCannotModifyBrokerProtection()
    {
        var modified = false;
        await Assert.ThrowsAsync<IOException>(() => AutomaticLiveExecutionService.PreparePartialProtectionAsync(Intent(), 50,
            () => throw new IOException("Database offline"),
            quantity => { modified = true; return Task.FromResult(Stop(quantity)); },
            () => Task.CompletedTask));
        Assert.False(modified);
    }

    private static LiveExecutionIntent Intent()
    {
        var result = new LiveExecutionIntent(Guid.NewGuid(), "MARKET_POSITION", Guid.NewGuid(), "SENSEX",
            Guid.NewGuid(), Direction.Buy, 100, 100m, 90m, 110m, "LE1234", DateTimeOffset.UtcNow);
        result.Filled(100, 100m);
        result.Protected("stop", DateTimeOffset.UtcNow);
        return result;
    }
    private static BrokerProtectionSnapshot Stop(int quantity) => new("LP1234", "stop", "TRIGGER_PENDING", quantity, 90m, DateTimeOffset.UtcNow);
}
