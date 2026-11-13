using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Api;
using TradingSystem.Application.Execution;

namespace TradingSystem.IntegrationTests;

public sealed class HistoricalGapReplayJobTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("gap-replay-job-tests-");
    private static readonly DateOnly Date = new(2026, 11, 12);
    private static readonly string[] ExpectedMarkets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    private static readonly string[] ExpectedRequests = ["nifty", "sensex", "sensex", "bank-nifty"];

    [Fact]
    public async Task LostAcknowledgmentRetryFindsTheSameJobEvenAfterItCompletes()
    {
        var clock = new Clock(new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var archive = new Archive();
        using var services = Services(archive);
        using var jobs = new HistoricalGapReplayJobs(clock, directory.FullName);
        var requestId = Guid.NewGuid();
        var original = Assert.IsType<GapReplayJob>(await jobs.EnqueueAsync(Date, "bank-nifty", default, requestId));
        Assert.Equal(requestId, original.Id);
        using var worker = Worker(jobs, services, clock);
        await worker.RunNextAsync(default);
        await worker.RunNextAsync(default);
        var retry = Assert.IsType<GapReplayJob>(await jobs.EnqueueAsync(Date, "bank-nifty", default, requestId));
        Assert.Equal(requestId, retry.Id);
        Assert.Equal("Completed", retry.Status);
        Assert.Null(await jobs.EnqueueAsync(Date, "nifty", default, requestId));
        await worker.RunNextAsync(default);
        Assert.Equal("bank-nifty", Assert.Single(archive.Requested));
    }

    [Fact]
    public async Task RestartResumesAtFirstUnfinishedMarketAndPublishesOneCompleteReport()
    {
        var clock = new Clock(new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var archive = new Archive();
        using var services = Services(archive);
        using var first = new HistoricalGapReplayJobs(clock, directory.FullName);
        var job = Assert.IsType<GapReplayJob>(await first.EnqueueAsync(Date, null, default));
        Assert.Equal(job.Id, (await first.EnqueueAsync(Date, null, default))!.Id);
        using (var worker = Worker(first, services, clock)) await worker.RunNextAsync(default);
        Assert.Equal("nifty", Assert.Single(archive.Requested));

        // Cancel a running market, as when Azure stops the website process.
        using (var shutdown = new CancellationTokenSource())
        using (var worker = Worker(first, services, clock))
        {
            archive.Interrupt = shutdown;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunNextAsync(shutdown.Token));
        }
        var interrupted = Assert.IsType<GapReplayJob>(await first.ReadAsync(job.Id, default));
        Assert.Equal("Running", interrupted.Status);
        Assert.Single(interrupted.CompletedMarkets!);
        Assert.Null(interrupted.Report);

        using var restarted = new HistoricalGapReplayJobs(clock, directory.FullName);
        using var replacement = Worker(restarted, services, clock);
        await replacement.RunNextAsync(default);
        await replacement.RunNextAsync(default);
        await replacement.RunNextAsync(default);
        var saved = Assert.IsType<GapReplayJob>(await restarted.ReadAsync(job.Id, default));
        Assert.Equal("Completed", saved.Status);
        Assert.Equal(ExpectedMarkets, saved.Report!.Markets.Select(m => m.Market));
        Assert.Equal(ExpectedRequests, archive.Requested);
    }

    [Fact]
    public async Task WorkerDefersDuringTradingAndStopsRetryingAfterThreeFailures()
    {
        var clock = new Clock(new(2026, 11, 13, 5, 0, 0, TimeSpan.Zero));
        var archive = new Archive { Fails = true };
        using var services = Services(archive);
        using var jobs = new HistoricalGapReplayJobs(clock, directory.FullName);
        var job = Assert.IsType<GapReplayJob>(await jobs.EnqueueAsync(Date, "bank-nifty", default));
        using var worker = Worker(jobs, services, clock);
        await worker.RunNextAsync(default);
        Assert.Empty(archive.Requested);
        clock.Now = clock.Now.AddHours(7);
        for (var i = 0; i < 4; i++) await worker.RunNextAsync(default);
        var failed = Assert.IsType<GapReplayJob>(await jobs.ReadAsync(job.Id, default));
        Assert.Equal("Failed", failed.Status);
        Assert.Equal(3, archive.Requested.Count);
        Assert.Null(failed.Report);
    }

    [Fact]
    public async Task SlowArchiveDoesNotConsumeSimulationTime()
    {
        var clock = new Clock(new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var archive = new Archive { OnRead = () => clock.Advance(TimeSpan.FromMinutes(9)),
            OnQuotes = () => clock.Advance(TimeSpan.FromMinutes(11)) };
        using var services = Services(archive);
        using var jobs = new HistoricalGapReplayJobs(clock, directory.FullName);
        var job = (await jobs.EnqueueAsync(Date, "bank-nifty", default))!;
        using var worker = Worker(jobs, services, clock);
        await worker.RunNextAsync(default);
        await worker.RunNextAsync(default);
        var saved = (await jobs.ReadAsync(job.Id, default))!;
        Assert.Equal("Completed", saved.Status);
        Assert.Null(Assert.Single(saved.Report!.Markets).RunError);
        Assert.Single(archive.Requested);
    }

    [Fact]
    public async Task SimulationTimeoutKeepsOtherMarketsAndDoesNotRetryTheSameWork()
    {
        var clock = new Clock(new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var archive = new Archive();
        using var services = Services(archive);
        using var jobs = new HistoricalGapReplayJobs(clock, directory.FullName);
        var job = (await jobs.EnqueueAsync(Date, null, default))!;
        using var worker = Worker(jobs, services, clock);
        await worker.RunNextAsync(default);
        archive.OnQuotes = () => clock.Advance(TimeSpan.FromMinutes(31));
        await worker.RunNextAsync(default);
        archive.OnQuotes = null;
        await worker.RunNextAsync(default);
        await worker.RunNextAsync(default);
        var saved = (await jobs.ReadAsync(job.Id, default))!;
        Assert.Equal("Completed", saved.Status);
        Assert.Collection(saved.Report!.Markets, m => Assert.Null(m.RunError),
            m => Assert.Contains("simulation", m.RunError), m => Assert.Null(m.RunError));
        Assert.Equal(3, archive.Requested.Count);
    }

    [Fact]
    public async Task SimulationBudgetNeverRunsAcrossTheTradingBoundary()
    {
        var clock = new Clock(new(2026, 11, 13, 3, 18, 0, TimeSpan.Zero));
        var archive = new Archive { OnRead = () => clock.Advance(TimeSpan.FromMinutes(9)),
            OnQuotes = () => clock.Advance(TimeSpan.FromMinutes(3)) };
        using var services = Services(archive);
        using var jobs = new HistoricalGapReplayJobs(clock, directory.FullName);
        var job = (await jobs.EnqueueAsync(Date, "bank-nifty", default))!;
        using var worker = Worker(jobs, services, clock);
        await worker.RunNextAsync(default);
        var saved = (await jobs.ReadAsync(job.Id, default))!;
        Assert.Equal("Queued", saved.Status);
        Assert.Equal(0, saved.Attempts);
        await worker.RunNextAsync(default);
        Assert.Single(archive.Requested);
    }

    private static ServiceProvider Services(Archive archive) => new ServiceCollection()
        .AddSingleton<IHistoricalReplayArchiveReader>(archive).AddSingleton<IHistoricalPolicyBuildStore, NoBuilds>()
        .AddScoped<HistoricalGapAwareReplayService>().BuildServiceProvider();
    private static HistoricalGapReplayWorker Worker(HistoricalGapReplayJobs jobs, ServiceProvider services, Clock clock) =>
        new(jobs, services.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<HistoricalGapReplayWorker>.Instance);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan elapsed)
        {
            Now += elapsed;
            foreach (var timer in timers.ToArray()) timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            return timer;
        }
        private sealed class ManualTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime;
                return true;
            }
            public void Fire()
            {
                if (due > clock.Now || due is null) return;
                due = null;
                callback(state);
            }
            public void Dispose() => clock.timers.Remove(this);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Archive : IHistoricalReplayArchiveReader
    {
        public List<string> Requested { get; } = [];
        public CancellationTokenSource? Interrupt { get; set; }
        public bool Fails { get; init; }
        public Action? OnRead { get; init; }
        public Action? OnQuotes { get; set; }
        public async Task<HistoricalReplayArchiveSlice> ReadAsync(DateOnly date, string market, CancellationToken token)
        {
            Requested.Add(market);
            if (Interrupt is { } shutdown) { Interrupt = null; await shutdown.CancelAsync(); token.ThrowIfCancellationRequested(); }
            if (Fails) throw new IOException("Simulated unavailable archive");
            OnRead?.Invoke();
            var symbol = market.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
            var start = new DateTimeOffset(2026, 11, 12, 3, 45, 0, TimeSpan.Zero);
            return new(new(date, symbol, [], new QuoteScan(OnQuotes, symbol, start), start, start.AddMinutes(375)), null, null, [], []);
        }
    }

    private sealed class QuoteScan(Action? onRead, string symbol, DateTimeOffset start) : IHistoricalChronologicalQuotePath
    {
        public int Count => 1;
        public HistoricalReplayQuote this[int index] => index == 0 ? new(symbol, start, 100) : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<HistoricalReplayQuote> GetEnumerator() { onRead?.Invoke(); yield return this[0]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class NoBuilds : IHistoricalPolicyBuildStore
    {
        public Task<HistoricalPolicyBuild> CaptureCurrentAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<HistoricalPolicyBuild>> ListAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IHistoricalPolicyModuleFactory> LoadAsync(string buildId, CancellationToken token) => throw new NotSupportedException();
    }
    public void Dispose() => directory.Delete(recursive: true);
}
