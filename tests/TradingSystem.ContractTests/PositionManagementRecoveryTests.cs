#pragma warning disable CA1861 // Small explicit expected call sequences in regression tests.
using Microsoft.EntityFrameworkCore;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class PositionManagementRecoveryTests
{
    [Fact]
    public async Task ConcurrencyReloadsInsteadOfReusingFailedTrackedState()
    {
        var attempts = new List<bool>();
        var stages = new List<string>();
        await PositionManagementRecovery.RunAsync(protectionOnly =>
        {
            attempts.Add(protectionOnly);
            if (attempts.Count == 1) throw new DbUpdateConcurrencyException();
            return Task.CompletedTask;
        }, (_, stage) => { stages.Add(stage); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(new[] { false, false }, attempts);
        Assert.Equal(new[] { "ConcurrencyReload" }, stages);
    }

    [Fact]
    public async Task AnalysisFailureFallsBackToProtectionAndDoesNotSkipNextPosition()
    {
        var calls = new List<bool>();
        await PositionManagementRecovery.RunAsync(protectionOnly =>
        {
            calls.Add(protectionOnly);
            if (!protectionOnly) throw new InvalidOperationException("analysis failed");
            return Task.CompletedTask;
        }, (_, _) => Task.CompletedTask, CancellationToken.None);
        await PositionManagementRecovery.RunAsync(protectionOnly =>
        {
            calls.Add(protectionOnly); return Task.CompletedTask;
        }, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(new[] { false, true, false }, calls);
    }

    [Fact]
    public async Task RepeatedConflictIsBoundedAndRecoveryFailureIsRecorded()
    {
        var calls = 0;
        var stages = new List<string>();
        await PositionManagementRecovery.RunAsync(_ =>
        {
            calls++; throw new DbUpdateConcurrencyException();
        }, (_, stage) => { stages.Add(stage); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(3, calls);
        Assert.Equal("ProtectiveRecoveryFailed", stages[^1]);
    }

    [Fact]
    public async Task CancellationNeverStartsRecovery()
    {
        using var source = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PositionManagementRecovery.RunAsync(_ =>
        {
            calls++; source.Cancel(); source.Token.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }, (_, _) => Task.CompletedTask, source.Token));
        Assert.Equal(1, calls);
    }
}
