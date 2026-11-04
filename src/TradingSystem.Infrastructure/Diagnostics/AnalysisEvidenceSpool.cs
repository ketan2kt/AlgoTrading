using System.Text.Json;

namespace TradingSystem.Infrastructure.Diagnostics;

internal sealed record AnalysisEvidenceRecord(Guid Id, string Severity,
    string EventType, string Message, DateTimeOffset OccurredAtUtc, string DetailsJson);

// A private, append-only fallback for diagnostic records, not order commands.
// Azure App Service's HOME is the persistent /home volume. Delete a record only
// after its idempotent database insert has been confirmed.
internal sealed class AnalysisEvidenceSpool(string directory)
{
    public async Task AppendAsync(AnalysisEvidenceRecord record, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{record.OccurredAtUtc.UtcTicks:D19}-{record.Id:N}.json");
        if (File.Exists(path)) return;
        var pending = path + ".pending";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        await using (var file = new FileStream(pending, FileMode.Create,
            FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await file.WriteAsync(bytes, token);
            await file.FlushAsync(token);
            file.Flush(flushToDisk: true);
        }
        File.Move(pending, path, overwrite: false);
    }

    public async Task FlushAsync(Func<AnalysisEvidenceRecord, CancellationToken, Task> persist,
        CancellationToken token)
    {
        if (!Directory.Exists(directory)) return;
        var paths = Directory.EnumerateFiles(directory, "*.json")
            .Order(StringComparer.Ordinal).Take(20).ToArray();
        var corrupt = new List<Exception>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            AnalysisEvidenceRecord record;
            try
            {
                record = JsonSerializer.Deserialize<AnalysisEvidenceRecord>(
                    await File.ReadAllTextAsync(path, token)) ??
                    throw new InvalidDataException("An analysis evidence record is empty.");
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                // Quarantine without deleting, so a damaged first batch cannot
                // permanently starve healthy records queued behind it.
                File.Move(path, path + ".corrupt", overwrite: false);
                corrupt.Add(exception);
                continue;
            }
            await persist(record, token);
            File.Delete(path);
        }
        if (corrupt.Count > 0)
            throw new AggregateException("Corrupt analysis evidence remains on disk for recovery.", corrupt);
    }

    public async Task RecoverPendingAsync(CancellationToken token)
    {
        if (!Directory.Exists(directory)) return;
        var corrupt = new List<Exception>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json.pending"))
        {
            try
            {
                _ = JsonSerializer.Deserialize<AnalysisEvidenceRecord>(
                    await File.ReadAllTextAsync(path, token)) ??
                    throw new InvalidDataException("An analysis evidence record is empty.");
                File.Move(path, path[..^".pending".Length], overwrite: false);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                File.Move(path, path + ".corrupt", overwrite: false);
                corrupt.Add(exception);
            }
        }
        if (corrupt.Count > 0)
            throw new AggregateException("Corrupt pending analysis evidence was retained for recovery.", corrupt);
    }
}
