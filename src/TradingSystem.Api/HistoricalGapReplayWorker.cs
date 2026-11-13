using TradingSystem.Api.Controllers;
using TradingSystem.Application.Execution;
using System.Text.Json;

namespace TradingSystem.Api;

public sealed partial class HistoricalGapReplayWorker(HistoricalGapReplayJobs jobs, IServiceScopeFactory scopes,
    TimeProvider clock, ILogger<HistoricalGapReplayWorker> logger) : BackgroundService
{
    private static readonly (string Key, string Label)[] Markets =
        [("nifty", "NIFTY"), ("sensex", "SENSEX"), ("bank-nifty", "BANKNIFTY")];
    private static readonly TimeSpan InputBudget = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SimulationBudget = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LedgerBudget = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ComparisonBudget = TimeSpan.FromHours(2);

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
            if (job.Kind != "gap-aware")
            {
                await RunComparisonAsync(job, stoppingToken);
                return;
            }
            var completed = job.CompletedMarkets?.ToList() ?? [];
            var remaining = Markets.Where(m => (job.Market is null || m.Key == job.Market) &&
                !completed.Any(saved => saved.Market == m.Label)).ToArray();
            var market = remaining.Length == 0 ? null : remaining[0].Key;
            if (job.Attempts >= HistoricalGapReplayJobs.MaximumAttempts)
            {
                await FinishUnavailableAsync(job, completed, remaining.FirstOrDefault().Label,
                    "The replay worker was interrupted repeatedly.", stoppingToken);
                return;
            }
            var phase = market is null ? "paper ledger" : "archive inputs";
            var phaseLimit = market is null ? LedgerBudget : InputBudget;
            var now = clock.GetUtcNow();
            var deadline = HistoricalReplayWorkWindowAttribute.AvailableTime(now, TimeSpan.MaxValue);
            if (deadline <= TimeSpan.Zero) return;
            var tradingDeadline = deadline == TimeSpan.MaxValue ? (DateTimeOffset?)null : now + deadline;
            bool limitedByTrading = false;
            job = job with { Status = "Running", Attempts = job.Attempts + 1,
                Phase = market is null ? "Comparing against the paper ledger" : $"Reading {market} archive" };
            await jobs.SaveAsync(job, stoppingToken);
            var timing = new HistoricalReplayTiming(sample => ReplayProgress(logger, job.Id, job.Date, market ?? "paper ledger",
                sample.Stage, sample.MarketTimeUtc, sample.QuotesProcessed, sample.OpenPositions, sample.ElapsedMs, sample.Totals));
            using var budget = new CancellationTokenSource();
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, budget.Token);
            using var timer = clock.CreateTimer(_ =>
            {
                try { budget.Cancel(); }
                catch (ObjectDisposedException) { /* The worker already left this stage. */ }
            }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                StartPhase(phase, phaseLimit);
                timing.Checkpoint("worker-start");
                await using var scope = scopes.CreateAsyncScope();
                if (market is not null)
                {
                    var replay = scope.ServiceProvider.GetRequiredService<HistoricalGapAwareReplayService>();
                    var result = await replay.RunAsync(job.Date, execution.Token, market, comparePaper: false,
                        inputsLoaded: () => StartPhase("simulation", SimulationBudget), timing: timing,
                        archiveReady: async token =>
                        {
                            job = job with { Phase = $"Simulating {market}" };
                            await jobs.SaveAsync(job, token);
                        });
                    execution.Token.ThrowIfCancellationRequested();
                    completed.Add(result.Markets.Single());
                    await jobs.SaveAsync(job with { Status = "Queued", Phase = null, Attempts = 0, CompletedMarkets = completed }, stoppingToken);
                    timing.Checkpoint("market-saved");
                    return;
                }
                var report = new HistoricalGapAwareReport(job.Date, completed);
                if (completed.Any(m => m.Windows.Any(w => w.Simulated && !w.IndependentFlatRestart)))
                {
                    var ledger = scope.ServiceProvider.GetRequiredService<IRecordedPaperSessionReader>();
                    using var measure = timing.Measure("paper-ledger");
                    report = report with { OpeningWindowFidelity = HistoricalGapAwareReplayService.CompareOpeningWindows(
                        completed, await ledger.ReadTradesAsync(job.Date, execution.Token)) };
                }
                execution.Token.ThrowIfCancellationRequested();
                await jobs.SaveAsync(job with { Status = "Completed", Phase = null, CompletedMarkets = null, Report = report }, stoppingToken);
                timing.Checkpoint("report-saved");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && limitedByTrading)
            { await jobs.SaveAsync(job with { Status = "Queued", Attempts = job.Attempts - 1 }, stoppingToken); }
            catch (OperationCanceledException exception) when (budget.IsCancellationRequested)
            {
                timing.Checkpoint("time-limit");
                JobError(logger, job.Id, market ?? "paper ledger", phase, exception);
                if (phase != "archive inputs" || job.Attempts >= HistoricalGapReplayJobs.MaximumAttempts)
                    await FinishUnavailableAsync(job, completed, remaining.FirstOrDefault().Label,
                        $"Replay exceeded its {phase} time limit.", stoppingToken);
                else await jobs.SaveAsync(job with { Status = "Queued" }, stoppingToken);
            }
            catch (Exception exception)
            {
                timing.Checkpoint("worker-error");
                JobError(logger, job.Id, market ?? "paper ledger", phase, exception);
                if (job.Attempts >= HistoricalGapReplayJobs.MaximumAttempts)
                    await FinishUnavailableAsync(job, completed, remaining.FirstOrDefault().Label,
                        $"Replay could not finish {phase} after three attempts.", stoppingToken);
                else await jobs.SaveAsync(job with { Status = "Queued" }, stoppingToken);
            }

            void StartPhase(string name, TimeSpan limit)
            {
                execution.Token.ThrowIfCancellationRequested();
                phase = name;
                var remainingTime = tradingDeadline - clock.GetUtcNow();
                limitedByTrading = remainingTime <= limit;
                if (remainingTime is { } remainingUntilTrading && remainingUntilTrading <= TimeSpan.Zero)
                {
                    budget.Cancel();
                    execution.Token.ThrowIfCancellationRequested();
                }
                timer.Change(limitedByTrading ? remainingTime!.Value : limit, Timeout.InfiniteTimeSpan);
            }
        }
        finally { HistoricalGapReplayJobs.ComputeSlot.Release(); }
    }

    private async Task RunComparisonAsync(GapReplayJob job, CancellationToken stoppingToken)
    {
        if (job.Attempts >= HistoricalGapReplayJobs.MaximumAttempts)
        {
            await jobs.SaveAsync(job with { Status = "Failed", Error = "The comparison was interrupted repeatedly." }, stoppingToken);
            return;
        }
        var available = HistoricalReplayWorkWindowAttribute.AvailableTime(clock.GetUtcNow(), ComparisonBudget);
        if (available <= TimeSpan.Zero) return;
        job = job with { Status = "Running", Attempts = job.Attempts + 1, Phase = "Checking recorded inputs" };
        await jobs.SaveAsync(job, stoppingToken);
        using var budget = new CancellationTokenSource(available, clock);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, budget.Token);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var request = job.ComparisonRequest;
            HistoricalNormalReplayComparisonReport? report = null;
            if (job.Kind == "paper-verification")
            {
                var recorded = scope.ServiceProvider.GetRequiredService<IRecordedPaperSessionReader>();
                var preflight = await recorded.PreflightAsync(job.Date, execution.Token);
                if (preflight.CaptureExclusion is { } reason)
                {
                    var cohort = new HistoricalReplayCohortSelection(Array.Empty<HistoricalReplaySessionData>(), [new(job.Date, [reason])]);
                    var excluded = new HistoricalSharedPortfolioResult(cohort, [], "", "", 0, 0, "InsufficientCoverage", [reason]);
                    report = new(excluded, ["Use gap-aware replay to inspect individual markets without treating unverified fills as matches."])
                    {
                        PaperFidelity = new("Not simulated: strict coverage incomplete", preflight.PaperTrades ?? 0, 0, [reason], [])
                    };
                }
                else
                {
                    var receipt = preflight.Receipt ?? throw new InvalidDataException(
                        "Verified pre-open policy is missing, conflicting, or lot settings changed. Current settings were not substituted.");
                    request = new(job.Date, job.Date, receipt.Settings, receipt.Settings, SessionPolicyReceipt.ComparisonExecution)
                    {
                        BaselineBuildId = receipt.BuildId, ProposalBuildId = receipt.BuildId, PaperSessionPolicyFingerprint = receipt.Fingerprint
                    };
                }
            }
            else if (job.Kind is not ("strategy-comparison" or "gap-comparison") || request is null || job.DefaultBuildId is null)
                throw new InvalidDataException("Saved comparison request is invalid.");
            else
                request = request with { BaselineBuildId = request.BaselineBuildId ?? job.DefaultBuildId,
                    ProposalBuildId = request.ProposalBuildId ?? job.DefaultBuildId };

            if (job.Kind == "gap-comparison")
            {
                var service = scope.ServiceProvider.GetRequiredService<HistoricalGapAwareReplayService>();
                var gapReport = await service.CompareAsync(request!, job.Market, PublishPhaseAsync, execution.Token);
                execution.Token.ThrowIfCancellationRequested();
                await jobs.SaveAsync(job with { Status = "Completed", Phase = null, GapComparisonReport = gapReport }, stoppingToken);
                return;
            }
            if (report is null)
            {
                var service = scope.ServiceProvider.GetRequiredService<HistoricalNormalReplayComparisonService>();
                report = await service.CompareAsync(request!, execution.Token, PublishPhaseAsync);
            }
            execution.Token.ThrowIfCancellationRequested();
            await jobs.SaveAsync(job with { Status = "Completed", Phase = null, ComparisonReport = report }, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && available < ComparisonBudget)
        {
            await jobs.SaveAsync(job with { Status = "Queued", Attempts = job.Attempts - 1,
                Phase = "Paused for the trading session" }, stoppingToken);
        }
        catch (Exception exception)
        {
            JobError(logger, job.Id, job.Kind, job.Phase ?? "comparison", exception);
            var error = exception switch
            {
                UnsupportedReplayContractException unsupported => unsupported.Message,
                OperationCanceledException when budget.IsCancellationRequested => $"Comparison exceeded its worker time budget during {job.Phase}. Choose a shorter date range.",
                InvalidDataException or JsonException => "Recorded settings, approved code, or a frozen input could not be verified. No substitute was used.",
                ArgumentException => "The frozen research configuration or date range is invalid.",
                _ => $"The replay worker could not finish during {job.Phase}."
            };
            await jobs.SaveAsync(job with { Status = "Failed", Error = error }, stoppingToken);
        }

        async Task PublishPhaseAsync(string phase, CancellationToken token)
        {
            job = job with { Phase = phase };
            await jobs.SaveAsync(job, token);
            ComparisonProgress(logger, job.Id, phase);
        }
    }

    private Task FinishUnavailableAsync(GapReplayJob job, List<HistoricalGapAwareMarket> completed,
        string? market, string message, CancellationToken token)
    {
        if (market is null && completed.Count != 0)
        {
            var report = new HistoricalGapAwareReport(job.Date, completed) { PaperComparisonError = message };
            return jobs.SaveAsync(job with { Status = "Completed", CompletedMarkets = null, Report = report }, token);
        }
        if (market is null || job.Market is not null)
            return jobs.SaveAsync(job with { Status = "Failed", Error = message }, token);
        completed.Add(new(market, 0, 0, false, [], [], null, [message]) { RunError = message });
        return jobs.SaveAsync(job with { Status = "Queued", Attempts = 0, CompletedMarkets = completed }, token);
    }

    [LoggerMessage(EventId = 6780, Level = LogLevel.Error, Message = "Historical replay job {JobId} failed for {Market} during {Stage}.")]
    private static partial void JobError(ILogger logger, Guid jobId, string market, string stage, Exception error);

    [LoggerMessage(EventId = 6781, Level = LogLevel.Error, Message = "Historical replay queue could not be read.")]
    private static partial void WorkerError(ILogger logger, Exception error);

    [LoggerMessage(EventId = 6783, Level = LogLevel.Information, Message = "Replay comparison {JobId}: {Stage}.")]
    private static partial void ComparisonProgress(ILogger logger, Guid jobId, string stage);

    [LoggerMessage(EventId = 6782, Level = LogLevel.Information, Message = "Replay job {JobId} {Date} {Market}: {Stage} after {ElapsedMs} ms; market clock {MarketTimeUtc}, quotes {Quotes}, open {Open}; phase totals {Totals}.")]
    private static partial void ReplayProgress(ILogger logger, Guid jobId, DateOnly date, string market,
        string stage, DateTimeOffset? marketTimeUtc, int quotes, int open, long elapsedMs, string totals);
}
