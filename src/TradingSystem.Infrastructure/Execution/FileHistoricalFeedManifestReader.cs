using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class FileHistoricalFeedManifestReader(string? directory = null, ReplayBlobStorage? cloud = null)
{
    private readonly string root = directory ?? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory,
        "data", "historical-replay", "feed");

    public async Task<HistoricalFeedCaptureManifest?> ReadCompleteAsync(DateOnly tradingDate,
        DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        var sourceRoot = root;
        if (cloud?.Enabled == true)
        {
            await cloud.HydrateAsync("feed", tradingDate, token);
            sourceRoot = Path.Combine(cloud.CacheRoot, "feed");
        }
        if (!Directory.Exists(sourceRoot)) return null;
        var paths = Directory.GetFiles(sourceRoot, tradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-*.json");
        // One retry per 30 seconds can exceed 100 without exhausting a session.
        if (paths.Length > 1000) throw new InvalidDataException("Too many historical feed manifests for one date.");
        foreach (var path in paths.OrderBy(value => value, StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || info.Length > 1024 * 1024)
                throw new InvalidDataException("Invalid historical feed manifest file.");
            var json = await File.ReadAllTextAsync(path, token);
            using var document = JsonDocument.Parse(json);
            var manifest = JsonSerializer.Deserialize<HistoricalFeedCaptureManifest>(json)
                ?? throw new InvalidDataException("An historical feed manifest is empty.");
            var rawPlan = document.RootElement.GetProperty("Instruments").GetRawText();
            if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPlan))) != manifest.UniverseFingerprint)
                throw new InvalidDataException("Historical feed universe integrity check failed.");
            if (HistoricalFeedCaptureEvidence.CoversWindow(manifest, tradingDate, start, end)) return manifest;
        }
        return null;
    }
}
