namespace TradingSystem.Infrastructure.Execution;

// Limits decompressed input without buffering the entire JSON document.
internal sealed class HistoricalBoundedReadStream(Stream source, long maximumBytes) : Stream
{
    private long consumed;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => consumed; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Account(source.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Account(source.Read(buffer));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Account(await source.ReadAsync(buffer, cancellationToken));
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Account(await source.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));
    private int Account(int count)
    {
        consumed += count;
        if (consumed > maximumBytes) throw new InvalidDataException("The research input exceeds its expanded size limit.");
        return count;
    }
}
