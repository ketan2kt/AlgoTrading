using System.Buffers.Binary;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed record GrowwFeedPoint(DateTimeOffset SourceAtUtc, decimal? Last,
    decimal? Volume, decimal? OpenInterest, decimal? Bid, long? BidQuantity,
    decimal? Offer, long? OfferQuantity);

// Wire field numbers follow Groww's MIT-licensed official SDK 1.5.0
// StocksSocketResponse.proto descriptor. No dynamic code loading or unsafe parser.
internal static class GrowwHistoricalFeedCodec
{
    public static GrowwFeedPoint Decode(ReadOnlySpan<byte> data, string kind,
        DateTimeOffset receivedAtUtc, string exchange, string segment)
    {
        if (data.Length is 0 or > 65536) throw new InvalidDataException("Invalid Groww feed frame size.");
        var reader = new ProtoReader(data);
        ReadOnlySpan<byte> body = default;
        var expected = kind switch { "index" => 6, "depth" => 5, "price" => 4, _ => throw new ArgumentException("Unknown feed kind.", nameof(kind)) };
        var wireExchange = 0; // Proto3 defaults are BSE and CASH.
        var wireSegment = 0;
        while (reader.Next(out var field, out var wire))
        {
            if (field == 3 && wire == 0) wireExchange = checked((int)reader.Varint());
            else if (field == 2 && wire == 0) wireSegment = checked((int)reader.Varint());
            else if (field == expected && wire == 2) body = reader.Bytes();
            else reader.Skip(wire);
        }
        if (body.IsEmpty || wireExchange != (exchange == "NSE" ? 1 : 0) || wireSegment != (segment == "FNO" ? 1 : 0))
            throw new InvalidDataException("Groww feed envelope does not match its subscription.");
        var payload = new ProtoReader(body);
        double stamp = 0;
        // Proto3 scalar defaults on the detailed-price topic are zero, as in
        // the official SDK's always_print_fields_with_no_presence decoder.
        decimal? last = null, volume = kind == "price" ? 0m : null,
            oi = kind == "price" ? 0m : null, bid = null, offer = null;
        long? bidQuantity = null, offerQuantity = null;
        while (payload.Next(out var field, out var wire))
        {
            if (wire == 1)
            {
                var value = payload.Double();
                if (field == 1) stamp = value;
                else if (kind == "index" && field == 2 || kind == "price" && field == 13) last = Price(value);
                else if (kind == "price" && field == 6) volume = NonNegative(value);
                else if (kind == "price" && field == 14) oi = NonNegative(value);
            }
            else if (kind == "depth" && wire == 2 && field is 2 or 3)
            {
                var level = Book(payload.Bytes());
                if (level is null) continue;
                if (field == 2 && (bid is null || level.Value.Price > bid)) { bid = level.Value.Price; bidQuantity = level.Value.Quantity; }
                if (field == 3 && (offer is null || level.Value.Price < offer)) { offer = level.Value.Price; offerQuantity = level.Value.Quantity; }
            }
            else payload.Skip(wire);
        }
        if (stamp is >= 1_000_000_000 and < 10_000_000_000) stamp *= 1000; // Official depth examples use seconds.
        if (!double.IsFinite(stamp) || stamp < 1_577_836_800_000 || stamp > receivedAtUtc.ToUnixTimeMilliseconds() + 1000)
            throw new InvalidDataException("Groww feed timestamp is invalid or in the future.");
        var source = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)stamp));
        if (kind != "depth" && last is not > 0m || kind == "depth" && (bid is not > 0m || offer is not > 0m || bid > offer))
            throw new InvalidDataException("Groww feed prices are absent or crossed.");
        return new(source, last, volume, oi, bid, bidQuantity, offer, offerQuantity);
    }

    private static (decimal Price, long Quantity)? Book(ReadOnlySpan<byte> bytes)
    {
        var entry = new ProtoReader(bytes);
        ReadOnlySpan<byte> value = default;
        while (entry.Next(out var field, out var wire))
            if (field == 2 && wire == 2) value = entry.Bytes(); else entry.Skip(wire);
        if (value.IsEmpty) return null;
        var book = new ProtoReader(value);
        decimal? price = null;
        long quantity = 0;
        while (book.Next(out var field, out var wire))
        {
            if (wire != 1) { book.Skip(wire); continue; }
            var number = book.Double();
            if (field == 1) price = Price(number);
            if (field == 2) quantity = checked((long)NonNegative(number));
        }
        return price is > 0m && quantity > 0 ? (price.Value, quantity) : null;
    }
    private static decimal Price(double value) => value is > 0 and < 1_000_000_000 && double.IsFinite(value)
        ? (decimal)value : throw new InvalidDataException("Invalid feed price.");
    private static decimal NonNegative(double value) => value is >= 0 and < 1_000_000_000_000_000 && double.IsFinite(value)
        ? (decimal)value : throw new InvalidDataException("Invalid feed quantity.");

    private ref struct ProtoReader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> remaining = bytes;
        public bool Next(out int field, out int wire)
        {
            if (remaining.IsEmpty) { field = wire = 0; return false; }
            var tag = Varint();
            field = checked((int)(tag >> 3)); wire = (int)(tag & 7);
            if (field == 0) throw new InvalidDataException("Invalid feed field.");
            return true;
        }
        public ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (remaining.IsEmpty) throw new InvalidDataException("Truncated feed field.");
                var current = remaining[0]; remaining = remaining[1..];
                if (shift == 63 && current > 1) throw new InvalidDataException("Feed varint overflow.");
                value |= (ulong)(current & 127) << shift;
                if ((current & 128) == 0) return value;
            }
            throw new InvalidDataException("Feed varint overflow.");
        }
        public ReadOnlySpan<byte> Bytes()
        {
            var length = checked((int)Varint());
            if (length > remaining.Length) throw new InvalidDataException("Truncated feed message.");
            var value = remaining[..length]; remaining = remaining[length..]; return value;
        }
        public double Double()
        {
            if (remaining.Length < 8) throw new InvalidDataException("Truncated feed number.");
            var value = BinaryPrimitives.ReadDoubleLittleEndian(remaining); remaining = remaining[8..]; return value;
        }
        public void Skip(int wire)
        {
            if (wire == 0) { Varint(); return; }
            if (wire == 2) { Bytes(); return; }
            var length = wire switch { 1 => 8, 5 => 4, _ => throw new InvalidDataException("Unsupported feed wire type.") };
            if (remaining.Length < length) throw new InvalidDataException("Truncated feed field.");
            remaining = remaining[length..];
        }
    }
}
