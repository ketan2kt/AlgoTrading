using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using TradingSystem.Application.Broker;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure;
using TradingSystem.Infrastructure.Persistence;
using Xunit;

namespace TradingSystem.IntegrationTests;

public sealed class PostgreSqlPersistenceTests
{
    [DockerFact]
    public async Task MigrationAndAuditPersistenceWorkAgainstPostgreSql()
    {
        await using var container = new PostgreSqlBuilder("postgres:18.4-alpine").Build();
        await container.StartAsync(CancellationToken.None);

        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .Options;
        await using var context = new TradingDbContext(options, TimeProvider.System);
        await context.Database.MigrateAsync(CancellationToken.None);

        var settingId = Guid.NewGuid();
        context.ApplicationSettings.Add(new ApplicationSetting(
            settingId,
            TradingMode.Paper,
            "Risk.MaximumTradesPerDay",
            """{"value":3}""",
            DateTimeOffset.UtcNow));
        context.AuditLogs.Add(new AuditLog(
            Guid.NewGuid(),
            "integration-test",
            "Created",
            nameof(ApplicationSetting),
            settingId.ToString(),
            "persistence verification",
            "{}",
            """{"value":3}""",
            Guid.NewGuid().ToString(),
            DateTimeOffset.UtcNow));
        await context.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(1, await context.ApplicationSettings.CountAsync());
        Assert.Equal(1, await context.AuditLogs.CountAsync());
    }

    [DockerFact]
    public async Task PaperBrokerStateIsReconstructedFromPostgreSqlAfterRestart()
    {
        await using var container = new PostgreSqlBuilder("postgres:18.4-alpine").Build();
        await container.StartAsync(CancellationToken.None);
        await using (var migrationContext = new TradingDbContext(
                         new DbContextOptionsBuilder<TradingDbContext>()
                             .UseNpgsql(container.GetConnectionString())
                             .Options,
                         TimeProvider.System))
        {
            await migrationContext.Database.MigrateAsync(CancellationToken.None);
        }

        var instrumentId = Guid.NewGuid();
        await using (var firstProvider = CreatePaperProvider(container.GetConnectionString()))
        {
            var gateway = firstProvider.GetRequiredService<IBrokerGateway>();
            var control = firstProvider.GetRequiredService<IPaperBrokerControl>();
            await gateway.SubmitAsync(new BrokerOrderRequest(
                "postgres-restart",
                instrumentId,
                Direction.Buy,
                7,
                22500m,
                3), CancellationToken.None);
            await control.ProcessNextFillAsync("postgres-restart", CancellationToken.None);
        }

        await using var restartedProvider = CreatePaperProvider(container.GetConnectionString());
        var restartedGateway = restartedProvider.GetRequiredService<IBrokerGateway>();
        var order = await restartedGateway.GetOrderAsync("postgres-restart", CancellationToken.None);
        var position = Assert.Single(
            await restartedGateway.GetPositionsAsync(CancellationToken.None));

        Assert.NotNull(order);
        Assert.Equal(OrderState.PartiallyFilled, order.State);
        Assert.Equal(3, order.FilledQuantity);
        Assert.Equal(instrumentId, position.InstrumentId);
        Assert.Equal(3, position.Quantity);
    }

    [DockerFact]
    public async Task GrowwTokenIsEncryptedAndCanBeRecoveredAfterRestart()
    {
        await using var container = new PostgreSqlBuilder("postgres:18.4-alpine").Build();
        await container.StartAsync(CancellationToken.None);
        await using (var migrationContext = new TradingDbContext(
                         new DbContextOptionsBuilder<TradingDbContext>()
                             .UseNpgsql(container.GetConnectionString())
                             .Options,
                         TimeProvider.System))
        {
            await migrationContext.Database.MigrateAsync(CancellationToken.None);
        }

        const string accessToken = "groww-integration-token-that-must-not-be-stored-plainly";
        await using var provider = CreatePaperProvider(container.GetConnectionString());
        await using (var scope = provider.CreateAsyncScope())
        {
            var vault = scope.ServiceProvider.GetRequiredService<IGrowwTokenVault>();
            var status = await vault.StoreAsync(
                accessToken,
                "integration-test",
                CancellationToken.None);

            Assert.True(status.IsConfigured);
            Assert.False(status.IsExpired);
        }

        await using (var verificationScope = provider.CreateAsyncScope())
        {
            var context = verificationScope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var persisted = await context.BrokerAccessTokenSecrets.SingleAsync();
            Assert.DoesNotContain(accessToken, persisted.ProtectedValue, StringComparison.Ordinal);
            Assert.NotEmpty(persisted.ProtectedValue);
            Assert.Equal(1, await context.AuditLogs.CountAsync());

            var vault = verificationScope.ServiceProvider.GetRequiredService<IGrowwTokenVault>();
            Assert.Equal(accessToken, await vault.GetValidTokenAsync(CancellationToken.None));
        }
    }

    private static ServiceProvider CreatePaperProvider(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TradingDatabase"] = connectionString,
                ["Trading:Mode"] = "Paper",
                ["IdentityBootstrap:Enabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTradingSystemInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}

public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_POSTGRES_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set RUN_POSTGRES_TESTS=true on a host with Docker to run.";
        }
    }
}
