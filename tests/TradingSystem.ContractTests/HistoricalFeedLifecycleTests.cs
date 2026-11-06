using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class HistoricalFeedLifecycleTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("sarthi-feed-lifecycle-").FullName;
    private static readonly DateOnly Date = new(2026, 11, 6);
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 9, 0, 0, TimeSpan.FromHours(5.5));
    private static readonly HistoricalFeedInstrument[] Plan = [
        new("NIFTY", "NSE", "CASH", "1", "NIFTY", InstrumentType.Index, null, null, 1, 0),
        new("SENSEX", "BSE", "CASH", "2", "SENSEX", InstrumentType.Index, null, null, 1, 0),
        new("BANKNIFTY", "NSE", "CASH", "3", "BANKNIFTY", InstrumentType.Index, null, null, 1, 0)];

    [Fact]
    public async Task PreOpenMetadataIsCheckpointedBeforeShutdownAndCrashIsNotCertifiedComplete()
    {
        var clock = new ManualClock(Start);
        var transport = new Transport(clock);
        var pulse = new ResearchCapturePulse(); pulse.Starting(Date);
        using var archive = new HistoricalQuoteArchive(clock, NullLogger<HistoricalQuoteArchive>.Instance, Path.Combine(directory, "quotes"));
        using var services = new ServiceCollection().BuildServiceProvider();
        using var worker = Worker(services, transport, archive, clock, pulse);
        using var lifetime = new CancellationTokenSource();
        var receipt = Receipt();
        var recording = worker.RecordSessionAsync(Date, Plan, (_, _) => Task.FromResult<SessionPolicyReceipt?>(receipt),
            (_, _) => Task.FromResult<(bool, PersistentDirectionalThesisV1?)>((true, null)), directory, lifetime.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UntilAsync(() => clock.ActiveTimers >= 3);
        clock.Advance(TimeSpan.FromSeconds(5));
        await UntilAsync(() => ReadManifest()?.PolicyReceipt is not null);
        var checkpoint = ReadManifest()!;
        Assert.True(checkpoint.PolicyReceipt!.ValidFor(Date));
        Assert.True(checkpoint.SensexPriorThesisStateCaptured);
        Assert.NotNull(checkpoint.LastCheckpointAtUtc);
        Assert.Null(checkpoint.CompletedAtUtc);
        Assert.Null(await new FileHistoricalFeedManifestReader(directory).ReadCompleteAsync(Date,
            Start.AddMinutes(15), Start.AddHours(6.5), CancellationToken.None));
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recording);
        Assert.True(ReadManifest()!.HasGaps);
        Assert.Equal(receipt.Fingerprint, ReadManifest()!.PolicyReceipt!.Fingerprint);
    }

    [Fact]
    public async Task CompletedLifecyclePersistsVerifiedManifest()
    {
        var clock = new ManualClock(Start);
        var transport = new Transport(clock);
        var pulse = new ResearchCapturePulse(); pulse.Starting(Date);
        using var archive = new HistoricalQuoteArchive(clock, NullLogger<HistoricalQuoteArchive>.Instance, Path.Combine(directory, "quotes"));
        using var services = new ServiceCollection().BuildServiceProvider();
        using var worker = Worker(services, transport, archive, clock, pulse);
        var recording = worker.RecordSessionAsync(Date, Plan, (_, _) => Task.FromResult<SessionPolicyReceipt?>(Receipt()),
            (_, _) => Task.FromResult<(bool, PersistentDirectionalThesisV1?)>((true, null)), directory, CancellationToken.None);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UntilAsync(() => clock.ActiveTimers >= 3);
        clock.Advance(TimeSpan.FromHours(6).Add(TimeSpan.FromMinutes(35)));
        await recording.WaitAsync(TimeSpan.FromSeconds(5));
        var restored = await new FileHistoricalFeedManifestReader(directory).ReadCompleteAsync(Date,
            Start.AddMinutes(15), Start.AddHours(6.5), CancellationToken.None);
        Assert.NotNull(restored);
        Assert.False(restored.HasGaps);
        Assert.Equal(3, restored.Instruments.Count);
        // Adding cloud configuration must not erase a completed local session
        // that predates recorder cutover.
        var legacy = await new FileHistoricalFeedManifestReader(directory,
            new EmptyCloud(Path.Combine(directory, "empty-cloud"))).ReadCompleteAsync(Date,
                Start.AddMinutes(15), Start.AddHours(6.5), CancellationToken.None);
        Assert.Equal(restored.UniverseFingerprint, legacy?.UniverseFingerprint);
        // This verifies lifecycle evidence, not complete market ticks: no replay
        // eligibility is asserted without bar and execution-path checks as well.
    }

    private sealed class EmptyCloud(string path) : IReplayArchiveCache
    {
        public bool Enabled => true;
        public string CacheRoot => path;
        public Task HydrateAsync(string category, DateOnly? tradingDate, CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public async Task SilentConnectedFeedIsCancelledAndGapIsPersisted()
    {
        var clock = new ManualClock(Start.AddMinutes(15));
        var transport = new Transport(clock);
        var pulse = new ResearchCapturePulse(); pulse.Starting(Date);
        using var archive = new HistoricalQuoteArchive(clock, NullLogger<HistoricalQuoteArchive>.Instance, Path.Combine(directory, "quotes"));
        using var services = new ServiceCollection().BuildServiceProvider();
        using var worker = Worker(services, transport, archive, clock, pulse);
        var recording = worker.RecordSessionAsync(Date, Plan, (_, _) => Task.FromResult<SessionPolicyReceipt?>(null),
            (_, _) => Task.FromResult<(bool, PersistentDirectionalThesisV1?)>((true, null)), directory, CancellationToken.None);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UntilAsync(() => clock.ActiveTimers >= 3);
        clock.Advance(TimeSpan.FromSeconds(31));
        await Assert.ThrowsAsync<TimeoutException>(() => recording.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(ReadManifest()!.HasGaps);
        Assert.True(archive.ReadHealth(Date).GapObserved);
        Assert.True(transport.Cancelled);
    }

    private HistoricalFeedCaptureManifest? ReadManifest()
    {
        var path = Directory.GetFiles(directory, "*.json").SingleOrDefault();
        return path is null ? null : JsonSerializer.Deserialize<HistoricalFeedCaptureManifest>(File.ReadAllText(path));
    }
    private static GrowwHistoricalFeedCaptureService Worker(ServiceProvider services, Transport transport,
        HistoricalQuoteArchive archive, ManualClock clock, ResearchCapturePulse pulse) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), transport, archive,
            Options.Create(new HistoricalFeedCaptureOptions()), clock, pulse, NullLogger<GrowwHistoricalFeedCaptureService>.Instance);
    private static SessionPolicyReceipt Receipt() => SessionPolicyReceipt.Create(Date, Start, Guid.NewGuid().ToString("N"),
        new HistoricalNormalPolicyConfiguration(60, 4, 2, new(9, 30), 5, 5, 10m, new(),
            new(true, 1.5m, 500m, 100m, 100m, 10m, 1m, 8, 100_000m, 1_000_000m, 100_000m,
                new(9, 15), new(15, 0), new(15, 15), 5, 5, true, .35m, 1m, .6m),
            new(new(15, 15), true, true, true, true, .6m, true, .55m, 3, true, .8m, 5, 5, 60),
            new(new(9, 15), new(15, 0), new(15, 15), 5, 60, 10m, 5),
            new(new(15, 15), true, true, true, .55m, 3, true, .8m, 5, 5, 60),
            new MarketRegimeOptions(), new PriceActionStrategyOptions(), new OpeningRangeBreakoutOptions()));
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        { if (condition()) return; await Task.Delay(10); }
        Assert.True(condition(), "Expected capture progress was not persisted.");
    }
    private sealed class Transport(TimeProvider clock) : IGrowwHistoricalFeedTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async Task ConsumeAsync(IReadOnlyList<HistoricalFeedInstrument> instruments,
            Action<HistoricalFeedInstrument, string, GrowwFeedPoint, DateTimeOffset> onPoint,
            Action onGap, Action<DateTimeOffset> onSubscribed, CancellationToken token)
        {
            onSubscribed(clock.GetUtcNow());
            foreach (var instrument in instruments)
                onPoint(instrument, "index", new(clock.GetUtcNow(), 22000, null, null, null, null, null, null), clock.GetUtcNow());
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Cancelled = true; }
        }
    }
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        public int ActiveTimers { get { lock (gate) return timers.Count; } }
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate) { var timer = new Timer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        }
        public void Advance(TimeSpan elapsed)
        {
            Timer[] due;
            lock (gate) { now += elapsed; due = timers.Where(timer => timer.Due <= now).ToArray();
                foreach (var timer in due) timer.Due = timer.Period > TimeSpan.Zero ? now + timer.Period : DateTimeOffset.MaxValue; }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class Timer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; set; }
            public TimeSpan Period { get; private set; }
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (owner.gate) { if (disposed) return false; Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.GetUtcNow() + dueTime; Period = period; return true; } }
            public void Fire() { lock (owner.gate) { if (!disposed) callback(state); } }
            public void Dispose() { lock (owner.gate) { disposed = true; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
