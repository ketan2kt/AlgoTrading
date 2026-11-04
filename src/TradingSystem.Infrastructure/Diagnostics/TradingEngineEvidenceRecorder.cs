using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Diagnostics;

internal sealed partial class TradingEngineEvidenceRecorder(
    IServiceScopeFactory scopeFactory, TimeProvider clock,
    ILogger<TradingEngineEvidenceRecorder> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Signature, DateTimeOffset At)> recorded = new();
    private readonly AnalysisEvidenceSpool spool = new(Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory,
        "data", "analysis-evidence"));
    private readonly ConcurrentQueue<AnalysisEvidenceRecord> emergencyQueue = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await gate.WaitAsync(stoppingToken);
            try { await spool.RecoverPendingAsync(stoppingToken); }
            finally { gate.Release(); }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { LogPersistenceFailure(logger, exception); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        do
        {
            try
            {
                await spool.FlushAsync(PersistAsync, stoppingToken);
                while (emergencyQueue.TryPeek(out var pending))
                {
                    await PersistAsync(pending, stoppingToken);
                    emergencyQueue.TryDequeue(out _);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { LogPersistenceFailure(logger, exception); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RecordAsync(string market, string engine, string phase,
        string status, string reason, DateTimeOffset atUtc, object evidence,
        CancellationToken token)
    {
        var local = atUtc.ToOffset(TimeSpan.FromHours(5.5));
        if (status is not ("Disabled" or "Faulted") &&
            (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
             TimeOnly.FromDateTime(local.DateTime) < new TimeOnly(9, 0) ||
             TimeOnly.FromDateTime(local.DateTime) > new TimeOnly(15, 30))) return;
        var key = market + ":" + engine;
        var signature = phase + ":" + status + ":" + reason;
        await gate.WaitAsync(token);
        try
        {
            // Identical polls are represented by one minute heartbeats. Every
            // phase/status/reason change, including recovery, is saved at once.
            if (!recorded.TryGetValue(key, out var prior) ||
                prior.Signature != signature || atUtc - prior.At >= TimeSpan.FromMinutes(1))
            {
                var details = JsonSerializer.Serialize(new
                {
                    schemaVersion = "engine-evidence-v1", market, engine, phase,
                    status, reason, observedAtUtc = atUtc, evidence
                });
                var message = $"{market.ToUpperInvariant()} {phase}: {reason}";
                var record = new AnalysisEvidenceRecord(Guid.NewGuid(),
                    status == "Faulted" ? "Error" : "Information",
                    $"EngineEvidence.{market}.{engine}",
                    message[..Math.Min(2000, message.Length)], atUtc, details);
                try { await spool.AppendAsync(record, token); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogSpoolFailure(logger, record.Id, exception);
                    // Keep DB retries off the trading loop. This emergency
                    // queue is not restart-durable; the critical log says so.
                    emergencyQueue.Enqueue(record);
                }
                recorded[key] = (signature, atUtc);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPersistenceFailure(logger, exception);
        }
        finally { gate.Release(); }
    }

    private async Task PersistAsync(AnalysisEvidenceRecord record, CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        if (await db.SystemEvents.AsNoTracking().AnyAsync(value => value.Id == record.Id, token)) return;
        db.SystemEvents.Add(new SystemEvent(record.Id, record.Severity, record.EventType,
            record.Message, record.OccurredAtUtc, record.DetailsJson));
        await db.SaveChangesAsync(token);
    }

    public static object FailureEvidence(Exception exception)
    {
        var cause = exception.GetBaseException();
        return new { exceptionType = cause.GetType().FullName,
            sqlState = (cause as PostgresException)?.SqlState };
    }

    public override void Dispose()
    {
        base.Dispose();
        gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Analysis evidence spool failed for {EventId}; attempting database storage.")]
    private static partial void LogSpoolFailure(ILogger logger, Guid eventId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Analysis evidence persistence is degraded; pending disk records will be retried.")]
    private static partial void LogPersistenceFailure(ILogger logger, Exception exception);
}
