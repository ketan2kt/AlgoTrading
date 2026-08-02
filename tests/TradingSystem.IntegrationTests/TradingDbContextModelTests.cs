using Microsoft.EntityFrameworkCore;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.IntegrationTests;

public sealed class TradingDbContextModelTests
{
    [Fact]
    public void ModelContainsModeScopedAndIdempotencyConstraints()
    {
        using var context = CreateContext();
        var settings = context.Model.FindEntityType(typeof(ApplicationSetting));
        var orders = context.Model.FindEntityType(typeof(TradingOrder));
        var signals = context.Model.FindEntityType(typeof(Signal));

        Assert.NotNull(settings);
        Assert.Contains(
            settings.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual(["Mode", "Key"]));
        Assert.NotNull(orders);
        Assert.Contains(
            orders.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual(["Mode", "ClientReference"]));
        Assert.NotNull(signals);
        Assert.Contains(
            signals.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Single().Name == "Fingerprint");
    }

    [Fact]
    public async Task AppendOnlyEntityCannotBeModified()
    {
        await using var context = CreateContext();
        var audit = new AuditLog(
            Guid.NewGuid(),
            "operator",
            "ConfigurationChanged",
            "ApplicationSetting",
            Guid.NewGuid().ToString(),
            "test",
            "{}",
            "{}",
            Guid.NewGuid().ToString(),
            DateTimeOffset.UtcNow);
        context.Attach(audit);
        context.Entry(audit).State = EntityState.Modified;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesAsync(CancellationToken.None));
    }

    private static TradingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model_only")
            .Options;
        return new TradingDbContext(options, TimeProvider.System);
    }
}

