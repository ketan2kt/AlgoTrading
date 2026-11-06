using System.Globalization;

namespace TradingSystem.Infrastructure.MarketData;

// Only disposable, downloaded quote shards are evicted. Never touch source
// recordings, cloud blobs, policy assemblies, or saved replay-result paths.
internal sealed class ReplayQuoteCacheBudget(string directory, long maximumBytes)
{
    private readonly object gate = new();
    private readonly Dictionary<DateOnly, int> readers = [];
    private readonly string root = Path.GetFullPath(directory);

    public IDisposable Pin(DateOnly tradingDate)
    {
        lock (gate) readers[tradingDate] = readers.GetValueOrDefault(tradingDate) + 1;
        return new Lease(this, tradingDate);
    }

    public void Reserve(long incomingBytes, long freeBytes, long reserveBytes)
    {
        if (incomingBytes < 0 || maximumBytes <= 0 || incomingBytes > maximumBytes)
            throw new IOException("A replay quote shard exceeds the configured cache budget.");
        lock (gate)
        {
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            if (new DirectoryInfo(root).LinkTarget is not null) throw new IOException("Linked replay cache directory is not permitted.");
            var files = new DirectoryInfo(root).EnumerateFiles().Where(IsQuoteCopy).ToArray();
            var used = files.Sum(file => file.Length);
            bool Fits() => used <= maximumBytes - incomingBytes &&
                (freeBytes < 0 || freeBytes >= incomingBytes + reserveBytes);
            if (Fits()) return;
            var groups = files.GroupBy(file => DateOnly.ParseExact(file.Name[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Where(group => !readers.ContainsKey(group.Key))
                .OrderBy(group => group.Max(file => file.LastAccessTimeUtc)).ToArray();
            foreach (var group in groups)
            {
                foreach (var file in group)
                {
                    // Revalidate each exact path before deletion; no recursive/glob deletion.
                    if (file.LinkTarget is not null || Path.GetDirectoryName(file.FullName) != root)
                        throw new IOException("Invalid replay cache path.");
                    var length = file.Length;
                    file.Delete();
                    used -= length;
                    if (freeBytes >= 0) freeBytes += length;
                }
                if (Fits()) return;
            }
            throw new IOException("Replay quote cache is full of active-session inputs; retry after the active replay or increase its cache budget. Cloud history is retained.");
        }
    }

    private static bool IsQuoteCopy(FileInfo file) => file.Name.Length >= 10 && file.LinkTarget is null &&
        DateOnly.TryParseExact(file.Name.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
        (file.Name.EndsWith(".ndjson.gz", StringComparison.Ordinal) || file.Name.EndsWith(".capture-gap", StringComparison.Ordinal));

    private sealed class Lease(ReplayQuoteCacheBudget owner, DateOnly tradingDate) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.gate)
            {
                if (--owner.readers[tradingDate] == 0) owner.readers.Remove(tradingDate);
            }
        }
    }
}
