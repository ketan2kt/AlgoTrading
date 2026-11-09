using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class FileHistoricalFeedManifestReader(string? directory = null, IReplayArchiveCache? cloud = null)
{
    private readonly string root = directory ?? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory,
        "data", "historical-replay", "feed");

    public async Task<HistoricalFeedCaptureManifest?> ReadCompleteAsync(DateOnly tradingDate,
        DateTimeOffset start, DateTimeOffset end, CancellationToken token)
        => (await ReadManifestsAsync(tradingDate, token))
            .FirstOrDefault(value => HistoricalFeedCaptureEvidence.CoversWindow(value, tradingDate, start, end));

    // Metadata from an interrupted attempt is still evidence. Never turn a
    // missing full-session feed into a false "pre-open settings missing" claim.
    // Consumers must independently require CoversWindow before certification.
    public async Task<HistoricalFeedCaptureManifest?> ReadEvidenceAsync(DateOnly tradingDate,
        DateTimeOffset start, DateTimeOffset end, CancellationToken token)
        => (await ReadManifestsAsync(tradingDate, token))
            .OrderByDescending(value => HistoricalFeedCaptureEvidence.CoversWindow(value, tradingDate, start, end))
            .ThenByDescending(value => value.PolicyReceipt?.ValidFor(tradingDate) == true)
            .ThenByDescending(value => value.ContractPreflight?.Ready == true)
            .ThenByDescending(value => value.LastCheckpointAtUtc ?? value.CompletedAtUtc ?? value.StartedAtUtc)
            .FirstOrDefault();

    private async Task<IReadOnlyList<HistoricalFeedCaptureManifest>> ReadManifestsAsync(DateOnly tradingDate,
        CancellationToken token)
    {
        var sourceRoots = new List<string> { root };
        if (cloud?.Enabled == true)
        {
            await cloud.HydrateAsync("feed", tradingDate, token);
            var remoteRoot = Path.Combine(cloud.CacheRoot, "feed");
            if (!string.Equals(Path.GetFullPath(root), Path.GetFullPath(remoteRoot), StringComparison.OrdinalIgnoreCase))
                sourceRoots.Add(remoteRoot);
        }
        var paths = sourceRoots.Where(Directory.Exists).SelectMany(source => Directory.GetFiles(source,
            tradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-*.json")).ToArray();
        // One retry per 30 seconds can exceed 100 without exhausting a session.
        if (paths.Length > 1000) throw new InvalidDataException("Too many historical feed manifests for one date.");
        var manifests = new List<HistoricalFeedCaptureManifest>();
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
            if (manifest.Date != tradingDate) throw new InvalidDataException("Historical feed manifest date mismatch.");
            manifests.Add(manifest);
        }
        var receipts = manifests.Select(value => value.PolicyReceipt).Where(value => value?.ValidFor(tradingDate) == true)
            .Select(value => value!.Fingerprint).Distinct(StringComparer.Ordinal).Take(2).Count();
        // A website receipt may be refreshed before the open. Different
        // observation times are fine, but different builds/settings are not.
        if (receipts > 1 && manifests.Select(value => value.PolicyReceipt).Where(value => value?.ValidFor(tradingDate) == true)
            .Select(value => JsonSerializer.Serialize(new { value!.BuildId, value.Settings })).Distinct(StringComparer.Ordinal).Take(2).Count() > 1)
            throw new InvalidDataException("Historical feed attempts have conflicting pre-open policies.");
        return manifests;
    }
}
