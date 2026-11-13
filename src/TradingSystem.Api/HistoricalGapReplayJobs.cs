using System.Text.Json;
using TradingSystem.Application.Execution;

namespace TradingSystem.Api;

public sealed record GapReplayJob(Guid Id, DateOnly Date, string? Market, string Status, DateTimeOffset CreatedAt,
    int Attempts = 0, IReadOnlyList<HistoricalGapAwareMarket>? CompletedMarkets = null,
    HistoricalGapAwareReport? Report = null, string? Error = null)
{
    public string Kind { get; init; } = "gap-aware";
    public HistoricalNormalReplayComparisonRequest? ComparisonRequest { get; init; }
    public HistoricalNormalReplayComparisonReport? ComparisonReport { get; init; }
    public HistoricalGapComparisonReport? GapComparisonReport { get; init; }
    public string? DefaultBuildId { get; init; }
    public DateOnly? From { get; init; }
    public string? Phase { get; init; }
}

// The mounted app data directory survives worker replacements. Each checkpoint
// is published atomically; the exclusive handle is released if the worker dies.
public sealed class HistoricalGapReplayJobs(TimeProvider clock, string? directory = null) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root = directory ?? Path.Combine(Environment.GetEnvironmentVariable("HOME") ??
        AppContext.BaseDirectory, "data", "historical-replay", "gap-jobs");
    private readonly SemaphoreSlim submissions = new(1, 1);
    public const int MaximumAttempts = 3;
    private const int MaximumActive = 6;
    private const int MaximumCompleted = 64;
    internal static SemaphoreSlim ComputeSlot { get; } = new(1, 1);

    public async Task<GapReplayJob?> EnqueueAsync(DateOnly date, string? market, CancellationToken token,
        Guid? requestId = null, string kind = "gap-aware", HistoricalNormalReplayComparisonRequest? comparison = null,
        string? defaultBuildId = null)
    {
        await submissions.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(root);
            // Keep admission atomic across rolling deployments, separately from the worker lock.
            await using var admission = TryLock("admission.lock");
            if (admission is null) return null;
            if (requestId is { } id && await ReadStateAsync(id, token) is { } previous)
                return Matches(previous) ? previous : null;
            var active = 0;
            var finished = new List<GapReplayJob>();
            foreach (var savedId in JobIds())
            {
                var job = await ReadStateAsync(savedId, token);
                if (job is null) continue;
                if (job.Status is "Queued" or "Running")
                {
                    if (Matches(job) && job.DefaultBuildId == defaultBuildId) return job;
                    active++;
                }
                else finished.Add(job);
            }
            if (active >= MaximumActive) return null;
            foreach (var old in finished.OrderByDescending(job => job.CreatedAt).Where((job, index) =>
                index >= MaximumCompleted || clock.GetUtcNow() - job.CreatedAt > TimeSpan.FromDays(7)))
            {
                File.Delete(JobPath(old.Id));
                File.Delete(ReportPath(old.Id));
                File.Delete(ComparisonPath(old.Id));
                File.Delete(GapComparisonPath(old.Id));
            }
            var next = new GapReplayJob(requestId ?? Guid.NewGuid(), date, market, "Queued", clock.GetUtcNow())
                { Kind = kind, ComparisonRequest = comparison, DefaultBuildId = defaultBuildId, From = comparison?.From };
            await SaveAsync(next, token);
            return next;
        }
        finally { submissions.Release(); }

        bool Matches(GapReplayJob other) => other.Date == date && other.Market == market && other.Kind == kind &&
            (comparison is null ? other.ComparisonRequest is null :
                other.ComparisonRequest is not null && JsonSerializer.Serialize(other.ComparisonRequest, Json) == JsonSerializer.Serialize(comparison, Json));
    }

    public async Task<GapReplayJob?> ReadAsync(Guid id, CancellationToken token)
    {
        var job = await ReadStateAsync(id, token);
        if (job?.Status != "Completed") return job;
        if (job.Kind == "gap-comparison")
        {
            var comparison = await ReadFileAsync<HistoricalGapComparisonReport>(GapComparisonPath(id), token);
            return comparison is null ? job with { Status = "Failed", Error = "The saved report is no longer available." } :
                job with { GapComparisonReport = comparison };
        }
        if (job.Kind != "gap-aware")
        {
            var comparison = await ReadFileAsync<HistoricalNormalReplayComparisonReport>(ComparisonPath(id), token);
            return comparison is null ? job with { Status = "Failed", Error = "The saved report is no longer available." } :
                job with { ComparisonReport = comparison };
        }
        var report = await ReadFileAsync<HistoricalGapAwareReport>(ReportPath(id), token);
        return report is null ? job with { Status = "Failed", Error = "The saved report is no longer available." } :
            job with { Report = report };
    }

    private Task<GapReplayJob?> ReadStateAsync(Guid id, CancellationToken token) => ReadFileAsync<GapReplayJob>(JobPath(id), token);

    private static async Task<T?> ReadFileAsync<T>(string path, CancellationToken token) where T : class
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 16384, useAsync: true);
            return await JsonSerializer.DeserializeAsync<T>(file, Json, token);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal FileStream? TryClaimWorker() => Directory.Exists(root) ? TryLock("worker.lock") : null;

    internal async Task<GapReplayJob?> NextAsync(CancellationToken token)
    {
        GapReplayJob? next = null;
        foreach (var id in JobIds())
        {
            var job = await ReadStateAsync(id, token);
            if (job is { Status: "Queued" or "Running" } && (next is null || job.CreatedAt < next.CreatedAt)) next = job;
        }
        return next;
    }

    internal async Task SaveAsync(GapReplayJob job, CancellationToken token)
    {
        if (job.Report is { } report) await WriteAsync(ReportPath(job.Id), report, token);
        if (job.ComparisonReport is { } comparison) await WriteAsync(ComparisonPath(job.Id), comparison, token);
        if (job.GapComparisonReport is { } gapComparison) await WriteAsync(GapComparisonPath(job.Id), gapComparison, token);
        await WriteAsync(JobPath(job.Id), job with { Report = null, ComparisonReport = null, GapComparisonReport = null }, token);
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16384, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(output, value, Json, token);
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private string JobPath(Guid id) => Path.Combine(root, id.ToString("N") + ".json");
    private string ReportPath(Guid id) => Path.Combine(root, id.ToString("N") + ".report");
    private string ComparisonPath(Guid id) => Path.Combine(root, id.ToString("N") + ".comparison");
    private string GapComparisonPath(Guid id) => Path.Combine(root, id.ToString("N") + ".gap-comparison");
    private IEnumerable<Guid> JobIds() => Directory.EnumerateFiles(root, "*.json")
        .Select(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty);
    private FileStream? TryLock(string name)
    {
        try { return new FileStream(Path.Combine(root, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    public void Dispose() => submissions.Dispose();
}
