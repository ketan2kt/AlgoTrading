using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

public sealed class PaperTradeResetOptions
{
    public const string SectionName = "PaperTradeReset";
    public bool Enabled { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public string BackupDirectory { get; init; } = "/home/site/data/trade-cleanup-backups";
}

internal sealed partial class PaperTradeResetService(
    IServiceScopeFactory scopeFactory,
    IOptions<PaperTradeResetOptions> options,
    TimeProvider timeProvider,
    ILogger<PaperTradeResetService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        if (!configuration.Enabled) return;
        if (string.IsNullOrWhiteSpace(configuration.RequestId))
            throw new InvalidOperationException("Paper-trade reset requires a non-empty request id.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var markerKey = $"PaperTradeReset:{configuration.RequestId.Trim()}";
        if (await db.ApplicationSettings.AsNoTracking().AnyAsync(value =>
                value.Mode == TradingMode.Paper && value.Key == markerKey, cancellationToken))
            return;

        var results = await db.PaperTradeResults.AsNoTracking().ToListAsync(cancellationToken);
        var positions = await db.MarketPaperPositions.AsNoTracking().ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        Directory.CreateDirectory(configuration.BackupDirectory);
        var safeRequestId = string.Concat(configuration.RequestId.Where(value =>
            char.IsLetterOrDigit(value) || value is '-' or '_'));
        var backupPath = Path.Combine(configuration.BackupDirectory,
            $"{safeRequestId}-{now:yyyyMMdd-HHmmss}Z.json.gz");
        await using (var file = File.Create(backupPath))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            await JsonSerializer.SerializeAsync(gzip, new PaperTradeBackup(now, results, positions),
                cancellationToken: cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var deletedResults = await db.PaperTradeResults.ExecuteDeleteAsync(cancellationToken);
        var deletedPositions = await db.MarketPaperPositions.ExecuteDeleteAsync(cancellationToken);
        var summary = JsonSerializer.Serialize(new
        {
            configuration.RequestId,
            DeletedNiftyResults = deletedResults,
            DeletedMarketPositions = deletedPositions,
            BackupPath = backupPath
        });
        db.ApplicationSettings.Add(new ApplicationSetting(Guid.NewGuid(), TradingMode.Paper,
            markerKey, summary, now));
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), "system", "PaperTradeHistoryReset",
            nameof(PaperTradeResult), configuration.RequestId, "User-authorized clean slate; analytical logs retained.",
            summary, summary, Guid.NewGuid().ToString("N"), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        ResetCompleted(logger, configuration.RequestId, deletedResults, deletedPositions);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed record PaperTradeBackup(DateTimeOffset CreatedAtUtc,
        IReadOnlyList<PaperTradeResult> NiftyResults,
        IReadOnlyList<MarketPaperPosition> MarketPositions);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Warning,
        Message = "Completed one-time paper trade reset {RequestId}: {Results} Nifty results and {Positions} market positions archived and removed.")]
    private static partial void ResetCompleted(ILogger logger, string requestId, int results,
        int positions);
}
