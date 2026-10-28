using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace TradingSystem.Application.MarketData;

public sealed record FedMonetaryRelease(string Title, DateTimeOffset PublishedAtUtc);

public static class FedMonetaryNewsParser
{
    public static FedMonetaryRelease? ParseLatest(string xml, DateTimeOffset asOfUtc)
    {
        using var input = new StringReader(xml.TrimStart('\uFEFF'));
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 200_000
        });
        var document = XDocument.Load(reader);
        return document.Descendants().Where(element => element.Name.LocalName == "item")
            .Select(item =>
            {
                var title = item.Elements().FirstOrDefault(value => value.Name.LocalName == "title")?.Value.Trim();
                var publishedText = item.Elements().FirstOrDefault(value =>
                    value.Name.LocalName == "pubDate")?.Value;
                return !string.IsNullOrWhiteSpace(title) && title.Length <= 180 &&
                       DateTimeOffset.TryParse(publishedText, CultureInfo.InvariantCulture,
                           DateTimeStyles.AssumeUniversal, out var published)
                    ? new FedMonetaryRelease(title, published.ToUniversalTime()) : null;
            })
            .OfType<FedMonetaryRelease>()
            .Where(value => value.PublishedAtUtc <= asOfUtc &&
                            value.PublishedAtUtc >= asOfUtc.AddHours(-36))
            .OrderByDescending(value => value.PublishedAtUtc)
            .FirstOrDefault();
    }
}
