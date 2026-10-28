using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace TradingSystem.Application.MarketData;

public sealed record EcbUsdInrReference(DateOnly Date, decimal RupeesPerDollar);

public static class EcbReferenceRateParser
{
    public static EcbUsdInrReference? ParseUsdInr(string xml)
    {
        using var input = new StringReader(xml.TrimStart('\uFEFF'));
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 100_000
        });
        var document = XDocument.Load(reader);
        var dated = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Cube" && element.Attribute("time") is not null);
        if (dated is null || !DateOnly.TryParseExact((string?)dated.Attribute("time"),
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;

        decimal? inr = null, usd = null;
        foreach (var currency in dated.Elements())
        {
            if (!decimal.TryParse((string?)currency.Attribute("rate"), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var rate) || rate <= 0) continue;
            switch ((string?)currency.Attribute("currency"))
            {
                case "INR": inr = rate; break;
                case "USD": usd = rate; break;
            }
        }
        return inr is > 0 && usd is > 0 ? new(date, inr.Value / usd.Value) : null;
    }
}
