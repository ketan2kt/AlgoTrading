using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
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
