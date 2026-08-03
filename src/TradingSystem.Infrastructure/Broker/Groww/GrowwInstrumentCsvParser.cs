using System.Globalization;
using System.Text;
using TradingSystem.Application.Broker;

namespace TradingSystem.Infrastructure.Broker.Groww;

internal static class GrowwInstrumentCsvParser
{
    private static readonly string[] RequiredColumns =
    [
        "exchange", "exchange_token", "trading_symbol", "groww_symbol",
        "instrument_type", "segment", "lot_size", "tick_size",
        "buy_allowed", "sell_allowed"
    ];

    public static IReadOnlyList<GrowwInstrumentRecord> Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            throw new GrowwApiException("Groww instrument master is empty.", "MALFORMED_RESPONSE");
        }

        var rows = ReadRows(csv);
        if (rows.Count < 2)
        {
            throw new GrowwApiException("Groww instrument master has no data rows.", "MALFORMED_RESPONSE");
        }

        var header = rows[0]
            .Select((name, index) => (Name: name.Trim(), Index: index))
            .ToDictionary(value => value.Name, value => value.Index, StringComparer.OrdinalIgnoreCase);
        var missing = RequiredColumns.Where(column => !header.ContainsKey(column)).ToArray();
        if (missing.Length > 0)
        {
            throw new GrowwApiException(
                $"Groww instrument master is missing columns: {string.Join(", ", missing)}.",
                "MALFORMED_RESPONSE");
        }

        var result = new List<GrowwInstrumentRecord>(rows.Count - 1);
        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            try
            {
                result.Add(new GrowwInstrumentRecord(
                    Required(row, header, "exchange"),
                    Required(row, header, "exchange_token"),
                    Required(row, header, "trading_symbol"),
                    Required(row, header, "groww_symbol"),
                    Required(row, header, "instrument_type"),
                    Required(row, header, "segment"),
                    Optional(row, header, "isin"),
                    Optional(row, header, "underlying_symbol"),
                    Optional(row, header, "expiry_date"),
                    OptionalDecimal(row, header, "strike_price"),
                    OptionalInt(row, header, "lot_size"),
                    OptionalDecimal(row, header, "tick_size"),
                    ParseBoolean(Required(row, header, "buy_allowed")),
                    ParseBoolean(Required(row, header, "sell_allowed"))));
            }
            catch (Exception exception) when (exception is FormatException or IndexOutOfRangeException)
            {
                // A malformed contract unrelated to the instrument being synchronized
                // must not make the complete master unusable. Required instruments are
                // validated by the synchronizer after parsing.
                _ = exception;
                continue;
            }
        }

        return result;
    }

    private static List<string[]> ReadRows(string csv)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (character == '"')
            {
                if (quoted && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if ((character == '\r' || character == '\n') && !quoted)
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        if (quoted)
        {
            throw new GrowwApiException("Groww instrument CSV has an unterminated quote.", "MALFORMED_RESPONSE");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }

    private static string Required(string[] row, IReadOnlyDictionary<string, int> header, string name)
    {
        var value = Get(row, header, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new FormatException($"Required field '{name}' is empty.")
            : value;
    }

    private static string? Optional(string[] row, IReadOnlyDictionary<string, int> header, string name) =>
        header.ContainsKey(name) && !string.IsNullOrWhiteSpace(Get(row, header, name))
            ? Get(row, header, name)
            : null;

    private static decimal? OptionalDecimal(
        string[] row,
        IReadOnlyDictionary<string, int> header,
        string name)
    {
        var value = Optional(row, header, name);
        return value is null ? null : ParseDecimal(value);
    }

    private static int? OptionalInt(
        string[] row,
        IReadOnlyDictionary<string, int> header,
        string name)
    {
        var value = Optional(row, header, name);
        return value is null ? null : ParseInt(value);
    }

    private static string Get(string[] row, IReadOnlyDictionary<string, int> header, string name) =>
        row[header[name]].Trim();

    private static int ParseInt(string value) =>
        int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(string value) =>
        decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static bool ParseBoolean(string value) => value.Trim() switch
    {
        "1" => true,
        "0" => false,
        _ => bool.Parse(value)
    };
}
