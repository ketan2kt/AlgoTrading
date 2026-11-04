using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TradingSystem.Application.Execution;

namespace TradingSystem.Infrastructure.Execution;

// Private, content-addressed JSON archives. No database/broker reads are needed
// when comparing a saved dataset again. Raw inputs never appear in list/report responses.
internal sealed class FileHistoricalNormalReplayRunStore(string? directory = null)
    : IHistoricalNormalReplayRunStore
{
    private readonly string root = directory ?? Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory,
        "data", "historical-replay", "runs");
    private const long MaximumCompressedBytes = 128L * 1024 * 1024;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;

    public async Task<string> SaveAsync(HistoricalNormalReplaySavedRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        Directory.CreateDirectory(root);
        var references = new Dictionary<string, HistoricalQuotePathReference>(StringComparer.Ordinal);
        var metadata = new List<HistoricalReplayArchiveSlice>();
        foreach (var slice in run.Inputs)
        {
            var path = await FileHistoricalReplayQuotePath.FreezeAsync(Path.Combine(root, "quote-paths"), slice.Session.Quotes, cancellationToken);
            references.Add(QuoteKey(slice), path.Reference);
            metadata.Add(slice with { Session = slice.Session with { Quotes = [] } });
        }
        run = run with { Inputs = metadata, QuotePaths = references };
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            await using (var file = File.Create(temporary))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                await JsonSerializer.SerializeAsync(gzip, run, cancellationToken: cancellationToken);
            if (new FileInfo(temporary).Length > MaximumCompressedBytes)
                throw new InvalidDataException("The frozen research run exceeds the archive size limit.");
            string id;
            await using (var file = File.OpenRead(temporary))
                id = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            var path = Path.Combine(root, id + ".json.gz");
            if (!File.Exists(path)) File.Move(temporary, path);
            var view = JsonSerializer.SerializeToUtf8Bytes(new HistoricalNormalReplaySavedView(run.Request, run.Report));
            var viewPath = Path.Combine(root, id + ".view.json");
            if (!File.Exists(viewPath))
            {
                await File.WriteAllBytesAsync(viewPath + ".pending", view, cancellationToken);
                File.Move(viewPath + ".pending", viewPath);
            }
            var c = run.Report.Comparison;
            var summary = new HistoricalNormalReplayRunSummary(id, run.CreatedAtUtc, run.Request.From,
                run.Request.To, run.Report.BaselineBuildId, run.Report.ProposalBuildId,
                c.Cohort.IncludedDates.Count, c.BaselineNet, c.ProposalNet)
                { ViewFingerprint = Convert.ToHexString(SHA256.HashData(view)) };
            var summaryPath = Path.Combine(root, id + ".summary.json");
            if (!File.Exists(summaryPath))
            {
                var pending = summaryPath + ".pending";
                await File.WriteAllBytesAsync(pending, JsonSerializer.SerializeToUtf8Bytes(summary), cancellationToken);
                File.Move(pending, summaryPath);
            }
            return id;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<HistoricalNormalReplaySavedView> ViewAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        var summary = await ReadSummaryAsync(Path.Combine(root, id + ".summary.json"), cancellationToken);
        var info = new FileInfo(Path.Combine(root, id + ".view.json"));
        if (!info.Exists || info.LinkTarget is not null || info.Length > 32L * 1024 * 1024)
            throw new InvalidDataException("The saved report is absent or invalid.");
        var json = await File.ReadAllBytesAsync(info.FullName, cancellationToken);
        if (Convert.ToHexString(SHA256.HashData(json)) != summary.ViewFingerprint)
            throw new InvalidDataException("The saved report failed its integrity check.");
        return JsonSerializer.Deserialize<HistoricalNormalReplaySavedView>(json)
            ?? throw new InvalidDataException("The saved report is empty.");
    }

    public async Task<HistoricalNormalReplaySavedRun> LoadAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        var path = Path.Combine(root, id + ".json.gz");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > MaximumCompressedBytes)
            throw new InvalidDataException("The saved research run is absent or invalid.");
        await using var file = File.OpenRead(path);
        if (Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)) != id)
            throw new InvalidDataException("The saved research run failed its integrity check.");
        file.Position = 0;
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var json = new HistoricalBoundedReadStream(gzip, MaximumExpandedBytes);
        var saved = await JsonSerializer.DeserializeAsync<HistoricalNormalReplaySavedRun>(json,
            cancellationToken: cancellationToken) ?? throw new InvalidDataException("The frozen research run is empty.");
        if (saved.QuotePaths.Count == 0) return saved; // Legacy bounded inline archive.
        if (saved.QuotePaths.Count != saved.Inputs.Count) throw new InvalidDataException("Frozen quote path references are incomplete.");
        var inputs = new List<HistoricalReplayArchiveSlice>();
        foreach (var slice in saved.Inputs)
        {
            if (slice.Session.Quotes.Count != 0 || !saved.QuotePaths.TryGetValue(QuoteKey(slice), out var reference))
                throw new InvalidDataException("Conflicting frozen quote inputs.");
            var quotes = await FileHistoricalReplayQuotePath.OpenAsync(Path.Combine(root, "quote-paths"), reference, cancellationToken);
            inputs.Add(slice with { Session = slice.Session with { Quotes = quotes } });
        }
        return saved with { Inputs = inputs, QuotePaths = new Dictionary<string, HistoricalQuotePathReference>() };
    }

    public async Task<IReadOnlyList<HistoricalNormalReplayRunSummary>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return [];
        var summaries = new List<HistoricalNormalReplayRunSummary>();
        foreach (var path in Directory.EnumerateFiles(root, "*.summary.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
        {
            summaries.Add(await ReadSummaryAsync(path, cancellationToken));
        }
        return summaries.OrderByDescending(value => value.CreatedAtUtc).ToArray();
    }

    private static async Task<HistoricalNormalReplayRunSummary> ReadSummaryAsync(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > 64 * 1024)
            throw new InvalidDataException("Invalid saved-run summary.");
        var summary = JsonSerializer.Deserialize<HistoricalNormalReplayRunSummary>(await File.ReadAllTextAsync(path, token))
            ?? throw new InvalidDataException("The saved-run summary is empty.");
        ValidateId(summary.Id);
        if (Path.GetFileName(path) != summary.Id + ".summary.json")
            throw new InvalidDataException("The saved-run summary identity is invalid.");
        return summary;
    }

    private static void ValidateId(string id)
    {
        if (id is null || id.Length != 64 || id.Any(value => value is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("Invalid saved research run identifier.", nameof(id));
    }

    private static string QuoteKey(HistoricalReplayArchiveSlice slice) =>
        slice.Session.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "|" + slice.Session.IndexSymbol;
}
