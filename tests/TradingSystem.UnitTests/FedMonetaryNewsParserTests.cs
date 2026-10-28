using TradingSystem.Application.MarketData;

namespace TradingSystem.UnitTests;

public sealed class FedMonetaryNewsParserTests
{
    [Fact]
    public void TakesOnlyPublishedRecentHeadline()
    {
        const string xml = """
            <rss version="2.0"><channel>
              <item><title>Old release</title><pubDate>Fri, 23 Oct 2026 18:00:00 GMT</pubDate></item>
              <item><title>Federal Reserve issues FOMC statement</title>
                <pubDate>Wed, 28 Oct 2026 18:00:00 GMT</pubDate></item>
              <item><title>Future release</title><pubDate>Fri, 30 Oct 2026 18:00:00 GMT</pubDate></item>
            </channel></rss>
            """;

        var release = FedMonetaryNewsParser.ParseLatest(xml,
            new DateTimeOffset(2026, 10, 29, 3, 15, 0, TimeSpan.Zero));

        Assert.NotNull(release);
        Assert.Equal("Federal Reserve issues FOMC statement", release.Title);
    }

    [Fact]
    public void StaleFeedProducesNoEvent()
    {
        const string xml = """
            <rss><channel><item><title>Old release</title>
              <pubDate>Fri, 23 Oct 2026 18:00:00 GMT</pubDate>
            </item></channel></rss>
            """;

        Assert.Null(FedMonetaryNewsParser.ParseLatest(xml,
            new DateTimeOffset(2026, 10, 29, 3, 15, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Utf8BomBeforeXmlDeclarationIsAccepted()
    {
        const string xml = "\uFEFF<?xml version=\"1.0\"?><rss><channel><item>" +
                           "<title>Federal Reserve issues statement</title>" +
                           "<pubDate>Wed, 28 Oct 2026 18:00:00 GMT</pubDate>" +
                           "</item></channel></rss>";

        Assert.NotNull(FedMonetaryNewsParser.ParseLatest(xml,
            new DateTimeOffset(2026, 10, 29, 3, 15, 0, TimeSpan.Zero)));
    }
}
