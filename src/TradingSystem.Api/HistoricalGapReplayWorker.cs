using TradingSystem.Api.Controllers;
using TradingSystem.Application.Execution;

namespace TradingSystem.Api;

public sealed partial class HistoricalGapReplayWorker(HistoricalGapReplayJobs jobs, IServiceScopeFactory scopes,
    TimeProvider clock, ILogger<HistoricalGapReplayWorker> logger) : BackgroundService
{
    private static readonly (string Key, string Label)[] Markets =
        [("nifty", "NIFTY"), ("sensex", "SENSEX"), ("bank-nifty", "BANKNIFTY")];
    private static readonly TimeSpan MarketBudget = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunNextAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { WorkerError(logger, exception); }
        }
    }

    public async Task RunNextAsync(CancellationToken stoppingToken)
    {
        if (!HistoricalReplayWorkWindowAttribute.IsAllowed(clock.GetUtcNow()) ||
            !await HistoricalGapReplayJobs.ComputeSlot.WaitAsync(0, stoppingToken)) return;
        try
        {
            await using var claim = jobs.TryClaimWorker();
            if (claim is null || await jobs.NextAsync(stoppingToken) is not { } job) return;
            if (job.Attempts >= HistoricalGapReplayJobs.MaximumAttempts)
            {
                await FailAsync(job, "Replay stopped after repeated worker interruptions. No incomplete result was reported.", stoppingToken);
                return;
            }
            var completed = job.CompletedMarkets?.ToList() ?? [];
            var remaining = Markets.Where(m => (job.Market is null || m.Key == job.Market) &&
                !completed.Any(saved => saved.Market == m.Label)).ToArray();
            var available = HistoricalReplayWorkWindowAttribute.AvailableTime(clock.GetUtcNow(), MarketBudget);
            if (available <= TimeSpan.Zero) return;
            job = job with { Status = "Running", Attempts = job.Attempts + 1 };
            await jobs.SaveAsync(job, stoppingToken);
            using var budget = new CancellationTokenSource(available, clock);
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, budget.Token);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (remaining.Length != 0)
                {
                    var replay = scope.ServiceProvider.GetRequiredService<HistoricalGapAwareReplayService>();
                    var result = await replay.RunAsync(job.Date, execution.Token, remaining[0].Key, comparePaper: false);
                    completed.Add(result.Markets.Single());
                    await jobs.SaveAsync(job with { Status = "Queued", Attempts = 0, CompletedMarkets = completed }, stoppingToken);
                    return;
                }
                var report = new HistoricalGapAwareReport(job.Date, completed);
                if (completed.Any(m => m.Windows.Any(w => w.Simulated && !w.IndependentFlatRestart)))
                {
                    var ledger = scope.ServiceProvider.GetRequiredService<IRecordedPaperSessionReader>();
                    report = report with { OpeningWindowFidelity = HistoricalGapAwareReplayService.CompareOpeningWindows(
                        completed, await ledger.ReadTradesAsync(job.Date, execution.Token)) };
                }
                await jobs.SaveAsync(job with { Status = "Completed", CompletedMarkets = null, Report = report }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && available < MarketBudget)
            { await jobs.SaveAsync(job with { Status = "Queued", Attempts = job.Attempts - 1 }, stoppingToken); }
            catch (Exception exception)
            {
                JobError(logger, job.Id, remaining.FirstOrDefault().Key ?? "paper ledger", exception);
                if (job.Attempts >= HistoricalGapReplayJobs.MaximumAttempts)
                    await FailAsync(job, "Replay could not finish this market or reconcile the paper ledger after three attempts. No incomplete result was reported.", stoppingToken);
                else await jobs.SaveAsync(job with { Status = "Queued" }, stoppingToken);
            }
        }
        finally { HistoricalGapReplayJobs.ComputeSlot.Release(); }
    }

    private Task FailAsync(GapReplayJob job, string message, CancellationToken token) =>
        jobs.SaveAsync(job with { Status = "Failed", Error = message }, token);

    [LoggerMessage(EventId = 6780, Level = LogLevel.Error, Message = "Historical replay job {JobId} failed during {Stage}.")]
    private static partial void JobError(ILogger logger, Guid jobId, string stage, Exception error);

    [LoggerMessage(EventId = 6781, Level = LogLevel.Error, Message = "Historical replay queue could not be read.")]
    private static partial void WorkerError(ILogger logger, Exception error);
}
