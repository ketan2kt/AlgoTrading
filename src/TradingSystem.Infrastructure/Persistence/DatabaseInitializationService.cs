using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TradingSystem.Infrastructure.Persistence;

public sealed partial class DatabaseInitializationService(
    IOptions<DatabaseInitializationOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseInitializationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ApplyMigrations)
        {
            return;
        }

        LogMigrationStarted(logger);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
        LogMigrationCompleted(logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1100, Level = LogLevel.Warning,
        Message = "Controlled database migration started.")]
    private static partial void LogMigrationStarted(ILogger logger);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information,
        Message = "Controlled database migration completed.")]
    private static partial void LogMigrationCompleted(ILogger logger);
}
