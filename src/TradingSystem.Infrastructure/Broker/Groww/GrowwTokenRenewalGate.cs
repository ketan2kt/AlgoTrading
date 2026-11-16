using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Broker.Groww;

internal sealed record GrowwRenewalState(DateTimeOffset NextAttemptAtUtc, DateTimeOffset[] Attempts,
    Guid Owner, int Failures, string Reason);
internal sealed record GrowwRenewalClaim(Guid? Owner, DateTimeOffset NextAttemptAtUtc, string Reason);

internal static class GrowwTokenRenewalPolicy
{
    // The broker allows 150 requests / 24h. Leave headroom for other approved clients.
    internal const int MaximumAttempts = 96;
    internal static GrowwRenewalState Reserve(GrowwRenewalState state, DateTimeOffset now)
    {
        var recent = state.Attempts.Where(at => at > now.AddHours(-24)).Order().ToArray();
        if (now < state.NextAttemptAtUtc) return state with { Attempts = recent };
        if (recent.Length >= MaximumAttempts)
            return state with { Attempts = recent, NextAttemptAtUtc = recent[0].AddHours(24),
                Reason = "Daily renewal request budget reached" };
        return new(now.AddMinutes(2), [.. recent, now], Guid.NewGuid(), state.Failures,
            "Token renewal in progress or previous attempt interrupted");
    }

    internal static GrowwRenewalState Failed(GrowwRenewalState state, DateTimeOffset now,
        int? statusCode, DateTimeOffset? retryAfter)
    {
        var failures = Math.Min(state.Failures + 1, 10);
        var minutes = statusCode == 429 ? Math.Min(60, 15 * Math.Pow(2, failures - 1))
            : statusCode is 401 or 403 ? 15 : Math.Min(15, Math.Pow(2, failures));
        var next = now.AddMinutes(minutes);
        if (retryAfter > next) next = retryAfter.Value;
        return state with { NextAttemptAtUtc = next, Failures = failures,
            Reason = statusCode is 401 or 403 ? "Groww approval or authentication required"
                : statusCode == 429 ? "Groww token renewal rate-limited"
                : "Groww token renewal temporarily unavailable" };
    }
}

internal interface IGrowwTokenRenewalGate
{
    Task<GrowwRenewalClaim> ClaimAsync(CancellationToken token);
    Task CompleteAsync(Guid owner, bool success, int? statusCode, DateTimeOffset? retryAfter, CancellationToken token);
}

// Shared non-secret state: survives process restarts and coordinates website/recorder.
// Reserve before HTTP so even a crash or timeout consumes one request from the budget.
internal sealed class GrowwTokenRenewalGate(TradingDbContext db, TimeProvider clock) : IGrowwTokenRenewalGate
{
    private const string Key = "Broker.Groww.TokenRenewal";
    public Task<GrowwRenewalClaim> ClaimAsync(CancellationToken token) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await LockAsync(token);
        var row = await db.ApplicationSettings.SingleOrDefaultAsync(x => x.Mode == TradingMode.Paper && x.Key == Key, token);
        var now = clock.GetUtcNow();
        var state = Read(row, now);
        var next = GrowwTokenRenewalPolicy.Reserve(state, now);
        Save(row, next, now);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new GrowwRenewalClaim(next.Owner != state.Owner ? next.Owner : null, next.NextAttemptAtUtc, next.Reason);
    });

    public Task CompleteAsync(Guid owner, bool success, int? statusCode, DateTimeOffset? retryAfter, CancellationToken token) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await LockAsync(token);
        var row = await db.ApplicationSettings.SingleAsync(x => x.Mode == TradingMode.Paper && x.Key == Key, token);
        var now = clock.GetUtcNow();
        var state = Read(row, now);
        if (state.Owner == owner)
        {
            var next = success ? state with { NextAttemptAtUtc = now, Failures = 0, Reason = "Token renewed" }
                : GrowwTokenRenewalPolicy.Failed(state, now, statusCode, retryAfter);
            Save(row, next, now);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
    });

    private Task<int> LockAsync(CancellationToken token) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(723048216501)", token);

    private static GrowwRenewalState Read(ApplicationSetting? row, DateTimeOffset now) => row is null
        ? new(now, [], Guid.Empty, 0, "Ready")
        : JsonSerializer.Deserialize<GrowwRenewalState>(row.ValueJson)
            ?? throw new InvalidOperationException("Groww renewal state is invalid; renewal stopped safely.");

    private void Save(ApplicationSetting? row, GrowwRenewalState state, DateTimeOffset now)
    {
        var json = JsonSerializer.Serialize(state);
        if (row is null) db.ApplicationSettings.Add(new(Guid.NewGuid(), TradingMode.Paper, Key, json, now));
        else row.ChangeValue(json, now);
    }
}
