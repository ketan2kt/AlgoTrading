using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingSystem.Application.Execution;

namespace TradingSystem.Infrastructure.Execution;

// Content-addressed immutable quote paths. External merge sort bounds working
// memory even when a single date contains millions of counterfactual quotes.
internal sealed class FileHistoricalReplayQuotePath(string path, HistoricalQuotePathReference reference)
    : IHistoricalChronologicalQuotePath
{
    private readonly string filePath = path;
    public HistoricalQuotePathReference Reference { get; } = reference;
    public int Count => Reference.Count;
    public HistoricalReplayQuote this[int index] => index >= 0 && index < Count ? this.Skip(index).First() : throw new ArgumentOutOfRangeException(nameof(index));
    public IEnumerator<HistoricalReplayQuote> GetEnumerator() => ReadFile(filePath, Count).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static async Task<FileHistoricalReplayQuotePath> OpenAsync(string root, HistoricalQuotePathReference reference,
        CancellationToken token)
    {
        if (reference.Id.Length != 64 || reference.Id.Any(value => !char.IsAsciiHexDigitUpper(value) && !char.IsAsciiDigit(value)) || reference.Count < 0)
            throw new InvalidDataException("Invalid quote path identity.");
        var path = Path.Combine(root, reference.Id + ".quotes.gz");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > 8L * 1024 * 1024 * 1024)
            throw new InvalidDataException("A frozen quote path is absent or invalid.");
        await using var file = File.OpenRead(path);
        if (Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != reference.Id)
            throw new InvalidDataException("Frozen quote path integrity check failed.");
        return new(path, reference);
    }

    internal static async Task<FileHistoricalReplayQuotePath> FreezeAsync(string root, IReadOnlyList<HistoricalReplayQuote> quotes,
        CancellationToken token)
    {
        Directory.CreateDirectory(root);
        if (quotes is FileHistoricalReplayQuotePath existing)
        {
            var target = Path.Combine(root, existing.Reference.Id + ".quotes.gz");
            if (!File.Exists(target))
            {
                var pending = target + "." + Guid.NewGuid().ToString("N") + ".pending";
                try
                {
                    await using (var input = File.OpenRead(existing.filePath))
                    await using (var output = File.Create(pending)) await input.CopyToAsync(output, token);
                    try { File.Move(pending, target, overwrite: false); }
                    catch (IOException) when (File.Exists(target)) { /* The winning immutable file is verified below. */ }
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
            }
            return await OpenAsync(root, existing.Reference, token);
        }
        return await BuildAsync(root, AsAsync(quotes, token), token);
    }

    internal static async Task<FileHistoricalReplayQuotePath> BuildAsync(string root, IAsyncEnumerable<HistoricalReplayQuote> source,
        CancellationToken token, int chunkSize = 50_000)
    {
        if (chunkSize is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var scratch = Directory.CreateDirectory(Path.Combine(root, ".build-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var chunks = new List<(string Path, int Count)>();
            var buffer = new List<HistoricalReplayQuote>(chunkSize);
            await foreach (var quote in source.WithCancellation(token))
            {
                buffer.Add(quote);
                if (buffer.Count < chunkSize) continue;
                await FlushAsync();
            }
            if (buffer.Count > 0 || chunks.Count == 0) await FlushAsync();
            var pass = 0;
            while (chunks.Count > 1)
            {
                var merged = new List<(string Path, int Count)>();
                foreach (var batch in chunks.Chunk(64))
                {
                    var target = Path.Combine(scratch, $"merge-{pass.ToString(CultureInfo.InvariantCulture)}-{merged.Count.ToString(CultureInfo.InvariantCulture)}.gz");
                    var paths = batch.Select(value => (IReadOnlyList<HistoricalReplayQuote>)new FileHistoricalReplayQuotePath(value.Path, new("", value.Count))).ToArray();
                    var count = await WriteAsync(target, HistoricalQuotePaths.Merge(paths), token);
                    merged.Add((target, count));
                    foreach (var prior in batch) File.Delete(prior.Path);
                }
                chunks = merged;
                pass++;
            }
            var result = chunks[0];
            string id;
            await using (var file = File.OpenRead(result.Path)) id = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            var final = Path.Combine(root, id + ".quotes.gz");
            if (!File.Exists(final))
            {
                try { File.Move(result.Path, final); }
                catch (IOException) when (File.Exists(final)) { /* A concurrent identical freeze won. */ }
            }
            return await OpenAsync(root, new(id, result.Count), token);

            async Task FlushAsync()
            {
                buffer.Sort(HistoricalQuoteOrderComparer.Instance);
                var target = Path.Combine(scratch, "chunk-" + chunks.Count.ToString(CultureInfo.InvariantCulture) + ".gz");
                chunks.Add((target, await WriteAsync(target, buffer, token)));
                buffer.Clear();
            }
        }
        finally
        {
            // Only this method's explicitly created scratch child is removed.
            if (Path.GetDirectoryName(scratch) == root && Path.GetFileName(scratch).StartsWith(".build-", StringComparison.Ordinal))
                Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task<int> WriteAsync(string path, IEnumerable<HistoricalReplayQuote> rows, CancellationToken token)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var writer = new StreamWriter(gzip, new UTF8Encoding(false), 65536);
        writer.NewLine = "\n";
        HistoricalReplayQuote? previous = null;
        var count = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (previous is not null)
            {
                var order = HistoricalQuoteOrderComparer.Instance.Compare(previous, row);
                if (order > 0 || order == 0 && previous != row) throw new InvalidDataException("Conflicting or unordered quote observations.");
                if (order == 0) continue;
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(row).AsMemory(), token);
            previous = row;
            count = checked(count + 1);
        }
        return count;
    }

    private static IEnumerable<HistoricalReplayQuote> ReadFile(string path, int expectedCount)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var count = 0;
        HistoricalReplayQuote? previous = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 65536) throw new InvalidDataException("Invalid quote record size.");
            var quote = JsonSerializer.Deserialize<HistoricalReplayQuote>(line) ?? throw new InvalidDataException("Empty quote record.");
            if (++count > expectedCount || previous is not null && HistoricalQuoteOrderComparer.Instance.Compare(previous, quote) >= 0)
                throw new InvalidDataException("Frozen quote count/order is invalid.");
            previous = quote;
            yield return quote;
        }
        if (count != expectedCount) throw new InvalidDataException("Frozen quote count does not match its manifest.");
    }

    private static async IAsyncEnumerable<HistoricalReplayQuote> AsAsync(IEnumerable<HistoricalReplayQuote> source,
        [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var quote in source) { token.ThrowIfCancellationRequested(); yield return quote; }
        await Task.CompletedTask;
    }
}
