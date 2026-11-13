using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TradingSystem.Application.Broker;
using System.Xml;
using System.Xml.Linq;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace TradingSystem.Infrastructure.MarketData;

// Private managed-identity storage. Local disk is a spool/cache, not the only
// historical copy. No credentials, orders or arbitrary paths enter this store.
public sealed class ReplayBlobStorage : BackgroundService, IReplayArchiveCache
{
    private const int QuoteHydrationConcurrency = 4;
    private const long MinimumFreeDiskBytes = 1024L * 1024 * 1024;
    private static readonly Action<ILogger, string, DateOnly?, long, Exception?> Hydrated =
        LoggerMessage.Define<string, DateOnly?, long>(LogLevel.Information,
            new EventId(6761, "ReplayArchiveHydrated"),
            "Replay archive hydration for {Category} on {Date} finished in {ElapsedMs} ms.");
    private static readonly Action<ILogger, string, DateOnly?, long, Exception?> HydrationInterrupted =
        LoggerMessage.Define<string, DateOnly?, long>(LogLevel.Warning,
            new EventId(6764, "ReplayArchiveHydrationInterrupted"),
            "Replay archive hydration for {Category} on {Date} stopped after {ElapsedMs} ms.");
    private readonly BlobContainerClient? container;
    private readonly bool recorder;
    private readonly TimeProvider clock;
    private readonly ILogger<ReplayBlobStorage> logger;
    private readonly IServiceProvider services;
    private readonly BlobClient? keyRing;
    private readonly string keyDirectory;
    private string? keyRingHash;
    private readonly SemaphoreSlim quoteHydrateGate = new(1, 1);
    private readonly SemaphoreSlim feedHydrateGate = new(1, 1);
    private readonly SemaphoreSlim policyHydrateGate = new(1, 1);
    private readonly ReplayQuoteCacheBudget quoteCache;
    private readonly Dictionary<string, (long Length, DateTime Modified)> uploaded = new(StringComparer.Ordinal);
    internal readonly string Root;
    public string CacheRoot { get; }
    public bool Enabled => container is not null;
    public string? LastFailure { get; private set; }
    public DateTimeOffset? LastUploadAtUtc { get; private set; }
    public async Task VerifyPublishedKeysAsync(CancellationToken token)
    {
        // A staged setting is not evidence that the background mirror has run.
        // Gate recorder startup on the actual, current website key-ring upload.
        if (keyRing is null || keyRingHash is null)
            throw new InvalidOperationException("Website key-ring publication is not complete.");
        var properties = await keyRing.GetPropertiesAsync(cancellationToken: token);
        if (properties.Value.ContentLength <= 0)
            throw new InvalidDataException("Published website key ring is empty.");
    }
    public async Task VerifyAccessAsync(CancellationToken token)
    {
        if (container is null) throw new InvalidOperationException("Private replay storage is not configured.");
        var blob = container.GetBlobClient("recorder-readiness.json");
        var expected = Guid.NewGuid().ToString("N");
        var written = await blob.UploadAsync(BinaryData.FromString(expected), overwrite: true, token);
        var read = await blob.DownloadContentAsync(new BlobDownloadOptions
            { Conditions = new BlobRequestConditions { IfMatch = written.Value.ETag } }, token);
        if (read.Value.Content.ToString() != expected) throw new InvalidDataException("Private archive round-trip mismatch.");
    }
    private static readonly Action<ILogger, string, Exception?> Failed = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(6760, "ReplayCloudArchiveFailed"),
        "Private replay archive synchronization failed ({FailureType}); local originals are retained.");

    public ReplayBlobStorage(IConfiguration configuration, TimeProvider clock, ILogger<ReplayBlobStorage> logger, IServiceProvider services,
        IHostEnvironment environment)
    {
        this.clock = clock;
        this.logger = logger;
        this.services = services;
        var configuredKeys = configuration["DataProtection:KeysPath"];
        keyDirectory = string.IsNullOrWhiteSpace(configuredKeys) ? Path.Combine(environment.ContentRootPath, ".data-protection-keys") : configuredKeys;
        Root = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory, "data", "historical-replay");
        // Azure mounts HOME on network storage. Keep disposable, verified
        // downloads and sort scratch on this worker's local disk.
        CacheRoot = configuration["ReplayStorage:CacheDirectory"] ??
            (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is { Length: > 0 }
                ? Path.Combine(Path.GetTempPath(), "sarthi-replay-cache") : Path.Combine(Root, "remote"));
        quoteCache = new ReplayQuoteCacheBudget(Path.Combine(CacheRoot, "quotes"),
            configuration.GetValue<long?>("ReplayStorage:MaximumQuoteCacheBytes") ?? 3L * 1024 * 1024 * 1024);
        recorder = configuration["ApplicationRole"] == "ReplayRecorder";
        var endpoint = configuration["ReplayStorage:ContainerUri"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var uri = ValidateContainerUri(endpoint);
            container = new BlobContainerClient(uri, new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
        }
        if (!recorder && configuration["ReplayStorage:KeyRingUri"] is { Length: > 0 } keyAddress)
        {
            var uri = new Uri(keyAddress);
            ValidateContainerUri(new Uri(uri, ".").AbsoluteUri.TrimEnd('/'));
            if (uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith("/keyring.xml", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid shared key ring address.");
            keyRing = new BlobClient(uri, new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
        }
    }

    internal static Uri ValidateContainerUri(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme != "https" || !uri.Host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal) ||
            uri.Query.Length != 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath.Trim('/').Split('/').Length != 1 || uri.AbsolutePath.Trim('/').Length == 0)
            throw new InvalidOperationException("Replay storage requires an Azure container HTTPS URI without credentials.");
        return uri;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (container is null) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), clock);
        do
        {
            try
            {
                // The pre-open settings deadline must not wait behind a large
                // quote/build backlog or an unrelated key-ring upload failure.
                if (!recorder) await PublishPolicyReceiptAsync(stoppingToken);
                await PublishKeysAsync(stoppingToken);
                await PublishAsync(stoppingToken); LastFailure = null;
                if (recorder)
                {
                    var now = clock.GetUtcNow();
                    var date = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
                    var feed = services.GetRequiredService<ResearchCapturePulse>().Read(date, true);
                    // The full history stays in the dated manifest. Keep the live
                    // heartbeat within its existing 64 KiB reader limit.
                    feed = feed with { OptionDepthAudit = feed.OptionDepthAudit
                        .Select(market => market with { Gaps = market.Gaps.TakeLast(8).ToArray() }).ToArray() };
                    var status = new ReplayRecorderSnapshot(date, now, feed,
                        services.GetRequiredService<IHistoricalQuoteArchive>().ReadHealth(date), LastUploadAtUtc);
                    await container.GetBlobClient("recorder-health.json").UploadAsync(
                        BinaryData.FromObjectAsJson(status), overwrite: true, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                LastFailure = exception.GetType().Name;
                Failed(logger, LastFailure, null);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishPolicyReceiptAsync(CancellationToken token)
    {
        var started = clock.GetUtcNow();
        var local = started.ToOffset(TimeSpan.FromHours(5.5));
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || TimeOnly.FromDateTime(local.DateTime) >= new TimeOnly(9, 15)) return;
        var date = DateOnly.FromDateTime(local.DateTime);
        await using var scope = services.CreateAsyncScope();
        var build = await scope.ServiceProvider.GetRequiredService<IHistoricalPolicyBuildStore>().CaptureCurrentAsync(token);
        var config = await scope.ServiceProvider.GetRequiredService<SessionPolicySettings>().ReadAsync(token);
        var at = clock.GetUtcNow();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        if (await db.AuditLogs.AsNoTracking().AnyAsync(value => value.Action == "IndexTradingLotsChanged" &&
                value.OccurredAtUtc >= started && value.OccurredAtUtc <= at, token)) return;
        var receipt = SessionPolicyReceipt.Create(date, at, build.Id, config);
        if (!receipt.ValidFor(date)) return;
        await container!.GetBlobClient("receipts/" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json")
            .UploadAsync(BinaryData.FromObjectAsJson(receipt), overwrite: true, token);
    }

    internal async Task<SessionPolicyReceipt?> ReadWebsitePolicyAsync(DateOnly date, CancellationToken token)
    {
        if (container is null) return null;
        try
        {
            var blob = container.GetBlobClient("receipts/" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");
            var properties = await blob.GetPropertiesAsync(cancellationToken: token);
            if (properties.Value.ContentLength > 1024 * 1024) throw new InvalidDataException("Invalid website policy receipt size.");
            var content = await blob.DownloadContentAsync(new BlobDownloadOptions
                { Conditions = new BlobRequestConditions { IfMatch = properties.Value.ETag } }, token);
            var receipt = content.Value.Content.ToObjectFromJson<SessionPolicyReceipt>();
            return receipt?.ValidFor(date) == true ? receipt : null;
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }

    private async Task PublishKeysAsync(CancellationToken token)
    {
        if (keyRing is null) return;
        var document = new XDocument(new XElement("repository"));
        if (Directory.Exists(keyDirectory))
            foreach (var file in new DirectoryInfo(keyDirectory).EnumerateFiles("*.xml").OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                if (file.LinkTarget is not null || file.Length > 1024 * 1024) throw new InvalidDataException("Invalid key-ring file.");
                using var reader = XmlReader.Create(file.FullName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                document.Root!.Add(XElement.Load(reader));
            }
        if (!document.Root!.Elements("key").Any()) throw new InvalidOperationException("No key ring available to mirror.");
        using var bytes = new MemoryStream();
        document.Save(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        if (hash == keyRingHash) return;
        bytes.Position = 0;
        await keyRing.UploadAsync(bytes, overwrite: true, token);
        keyRingHash = hash;
    }

    public async Task<ReplayRecorderSnapshot?> ReadRecorderHealthAsync(CancellationToken token)
    {
        if (container is null || recorder) return null;
        try
        {
            var blob = container.GetBlobClient("recorder-health.json");
            var properties = await blob.GetPropertiesAsync(cancellationToken: token);
            if (properties.Value.ContentLength > 65536) throw new InvalidDataException("Invalid recorder heartbeat size.");
            var value = await blob.DownloadContentAsync(new BlobDownloadOptions
                { Conditions = new BlobRequestConditions { IfMatch = properties.Value.ETag } }, token);
            return value.Value.Content.ToObjectFromJson<ReplayRecorderSnapshot>();
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }

    private async Task PublishAsync(CancellationToken token)
    {
        if (container is null) return;
        foreach (var category in recorder ? new[] { "quotes", "feed", "policies" } : new[] { "policies", "quotes" })
        {
            var directory = Path.Combine(Root, category);
            if (!Directory.Exists(directory)) continue;
            var websiteQuoteUploads = 0;
            foreach (var path in Directory.EnumerateFiles(directory, "*", category == "policies" ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                var file = new FileInfo(path);
                var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
                if (!Allowed(relative) || file.LinkTarget is not null) continue;
                // Never upload an actively appended gzip member. Atomic manifests
                // can change during upload; the stable snapshot is replaced next pass.
                if (category == "quotes" && clock.GetUtcNow().UtcDateTime - file.LastWriteTimeUtc < TimeSpan.FromMinutes(2)) continue;
                if (category == "quotes" && !recorder) relative = WebsiteQuotePath(relative);
                var signature = (file.Length, file.LastWriteTimeUtc);
                if (!uploaded.TryGetValue(relative, out var previous) || previous != signature)
                {
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                    input.Position = 0;
                    await container.GetBlobClient(relative).UploadAsync(input, new BlobUploadOptions
                    {
                        Metadata = new Dictionary<string, string> { ["sha256"] = hash }
                    }, token);
                    file.Refresh();
                    if ((file.Length, file.LastWriteTimeUtc) != signature) continue;
                    uploaded[relative] = signature;
                    LastUploadAtUtc = clock.GetUtcNow();
                    if (category == "quotes" && !recorder && ++websiteQuoteUploads >= 24) break;
                }
                // Only evict verified uploaded quote spool files from older days.
                // Keep manifests/builds and today's spool; never delete cloud history.
                if (category == "quotes" && DateOnly.TryParseExact(file.Name.AsSpan(0, Math.Min(10, file.Name.Length)),
                        "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                    date < DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5)).DateTime).AddDays(-1))
                {
                    var properties = await container.GetBlobClient(relative).GetPropertiesAsync(cancellationToken: token);
                    if (properties.Value.ContentLength == file.Length && properties.Value.Metadata.TryGetValue("sha256", out var storedHash))
                    {
                        await using (var original = File.OpenRead(path))
                            if (Convert.ToHexString(await SHA256.HashDataAsync(original, token)) != storedHash)
                                throw new InvalidDataException("Cloud confirmation differs from local spool; original retained.");
                        file.Delete();
                    }
                }
            }
        }
    }

    internal static string WebsiteQuotePath(string relative) => relative.EndsWith(".ndjson.gz", StringComparison.Ordinal)
        ? relative[..^".ndjson.gz".Length] + "-website.ndjson.gz"
        : relative[..^".capture-gap".Length] + "-website.capture-gap";

    internal static bool IsWebsiteQuotePath(string path) => path.EndsWith("-website.ndjson.gz", StringComparison.Ordinal) ||
        path.EndsWith("-website.capture-gap", StringComparison.Ordinal);

    internal static bool Allowed(string relative)
    {
        if (relative.Contains("..", StringComparison.Ordinal) || relative.Contains('\\') || relative.StartsWith('/')) return false;
        var parts = relative.Split('/');
        if (parts.Length == 2 && parts[0] is "quotes" or "feed")
            return parts[1].Length >= 10 && DateOnly.TryParseExact(parts[1][..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _) && (parts[0] == "feed" ? parts[1].EndsWith(".json", StringComparison.Ordinal) :
                parts[1].EndsWith(".ndjson.gz", StringComparison.Ordinal) || parts[1].EndsWith(".capture-gap", StringComparison.Ordinal));
        return parts.Length == 3 && parts[0] == "policies" && Guid.TryParseExact(parts[1], "N", out _) &&
            parts[2] is "manifest.json" or "TradingSystem.Application.dll" or "TradingSystem.Domain.dll";
    }

    public IDisposable Pin(DateOnly tradingDate) => quoteCache.Pin(tradingDate);

    public Task HydrateAsync(string category, DateOnly? tradingDate, CancellationToken token) =>
        HydrateCoreAsync(category, tradingDate, streamingOnly: false, token);

    public Task HydrateStreamingQuotesAsync(DateOnly tradingDate, CancellationToken token) =>
        HydrateCoreAsync("quotes", tradingDate, streamingOnly: true, token);

    internal Task HydratePolicyAsync(string buildId, CancellationToken token)
    {
        if (!Guid.TryParseExact(buildId, "N", out var parsed) || buildId != parsed.ToString("N"))
            throw new ArgumentException("Invalid archived build identifier.", nameof(buildId));
        return HydrateCoreAsync("policies", null, streamingOnly: false, token, buildId);
    }

    internal Task HydratePolicyCatalogueAsync(CancellationToken token) =>
        HydrateCoreAsync("policies", null, streamingOnly: false, token, manifestsOnly: true);

    private async Task HydrateCoreAsync(string category, DateOnly? tradingDate, bool streamingOnly, CancellationToken token,
        string? policyId = null, bool manifestsOnly = false)
    {
        var date = tradingDate;
        if (container is null) return;
        if (category is not ("quotes" or "feed" or "policies") || category != "policies" && date is null)
            throw new ArgumentException("A bounded archive category/date is required.");
        using var pinned = date is { } requested ? Pin(requested) : null;
        var hydrateGate = category switch
        {
            "quotes" => quoteHydrateGate, "feed" => feedHydrateGate, _ => policyHydrateGate
        };
        var started = Stopwatch.GetTimestamp();
        await hydrateGate.WaitAsync(token);
        try
        {
            if (category == "quotes")
            {
                var disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(CacheRoot))!);
                quoteCache.Reserve(0, disk.IsReady ? disk.AvailableFreeSpace : -1, MinimumFreeDiskBytes);
            }
            var prefix = category + "/" + (policyId is null
                ? date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : policyId + "/");
            var capacity = category == "quotes" ? QuoteHydrationConcurrency : 1;
            var pendingDownloads = new List<(BlobItem Item, string Path, string Hash)>(capacity);
            await foreach (var item in container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix: prefix, cancellationToken: token))
            {
                if (streamingOnly && IsWebsiteQuotePath(item.Name)) continue;
                if (manifestsOnly && !item.Name.EndsWith("/manifest.json", StringComparison.Ordinal)) continue;
                if (!Allowed(item.Name) || !item.Metadata.TryGetValue("sha256", out var hash))
                    throw new InvalidDataException("Invalid replay cloud artifact.");
                var path = Path.Combine(CacheRoot, item.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length == item.Properties.ContentLength)
                {
                    await using var existing = File.OpenRead(path);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(existing, token)) == hash)
                    {
                        File.SetLastAccessTimeUtc(path, clock.GetUtcNow().UtcDateTime);
                        continue;
                    }
                }
                pendingDownloads.Add((item, path, hash));
                if (pendingDownloads.Count == capacity) await DownloadBatchAsync();
            }
            if (pendingDownloads.Count > 0) await DownloadBatchAsync();
            Hydrated(logger, category, date, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);

            async Task DownloadBatchAsync()
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(CacheRoot))!);
                var bytes = pendingDownloads.Sum(value => value.Item.Properties.ContentLength
                    ?? throw new InvalidDataException("Replay cloud artifact size is missing."));
                var free = drive.IsReady ? drive.AvailableFreeSpace : -1;
                // Reserve all simultaneous temporary files before any download
                // starts. The hydration gate holds until every task has exited.
                if (category == "quotes") quoteCache.Reserve(bytes, free, MinimumFreeDiskBytes);
                else if (free >= 0 && free < bytes + MinimumFreeDiskBytes)
                    throw new IOException("Replay cache needs additional free space; cloud history is retained.");
                await Task.WhenAll(pendingDownloads.Select(value => DownloadAsync(value.Item, value.Path, value.Hash)));
                pendingDownloads.Clear();
            }

            async Task DownloadAsync(BlobItem item, string path, string hash)
            {
                var pending = path + ".pending";
                try
                {
                    var blob = container.GetBlobClient(item.Name);
                    await ReadStableBlobAsync(async (refresh, ct) =>
                    {
                        var properties = refresh ? (await blob.GetPropertiesAsync(cancellationToken: ct)).Value : null;
                        var etag = properties?.ETag ?? item.Properties.ETag;
                        var length = properties?.ContentLength ?? item.Properties.ContentLength;
                        var checksum = hash;
                        if (properties is not null)
                        {
                            if (!properties.Metadata.TryGetValue("sha256", out var refreshedChecksum))
                                throw new InvalidDataException("Replay cloud artifact checksum is missing.");
                            checksum = refreshedChecksum;
                        }
                        if (length is null || length < 0 || category == "quotes" && length > item.Properties.ContentLength)
                            throw new InvalidDataException("Replay cloud artifact changed beyond its reserved size; retry on a new archive snapshot.");
                        if (category != "quotes" && length > item.Properties.ContentLength)
                        {
                            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(CacheRoot))!);
                            if (drive.IsReady && drive.AvailableFreeSpace < length + MinimumFreeDiskBytes)
                                throw new IOException("Replay cache needs additional free space; cloud history is retained.");
                        }
                        // An ETag failure can leave a partial file. Recreate it before retrying.
                        await using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                            await blob.DownloadToAsync(output, new BlobDownloadToOptions
                            {
                                Conditions = new BlobRequestConditions { IfMatch = etag },
                                TransferOptions = new Azure.Storage.StorageTransferOptions { MaximumConcurrency = 1 }
                            }, ct);
                        if (new FileInfo(pending).Length != length)
                            throw new InvalidDataException("Replay cloud artifact size mismatch.");
                        await using var input = File.OpenRead(pending);
                        if (Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != checksum)
                            throw new InvalidDataException("Replay cloud artifact checksum mismatch.");
                    }, token);
                    File.Move(pending, path, overwrite: true);
                    File.SetLastAccessTimeUtc(path, clock.GetUtcNow().UtcDateTime);
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
            }
        }
        catch (OperationCanceledException)
        {
            HydrationInterrupted(logger, category, date, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
            throw;
        }
        finally { hydrateGate.Release(); }
    }

    internal static async Task ReadStableBlobAsync(Func<bool, CancellationToken, Task> read, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { await read(attempt > 0, token); return; }
            catch (RequestFailedException error) when (error.Status == 412 && attempt < 2) { }
        }
    }

    public override void Dispose()
    {
        quoteHydrateGate.Dispose();
        feedHydrateGate.Dispose();
        policyHydrateGate.Dispose();
        base.Dispose();
    }
}

public sealed record ReplayRecorderSnapshot(DateOnly Date, DateTimeOffset AtUtc,
    ResearchCaptureSnapshot Feed, HistoricalQuoteArchiveHealth? Storage, DateTimeOffset? LastUploadAtUtc);
