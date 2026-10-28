using TradingSystem.Application.MarketData;

namespace TradingSystem.UnitTests;

public sealed class EcbReferenceRateParserTests
{
    [Fact]
    public void CalculatesUsdInrFromDatedOfficialEuroRates()
    {
        const string xml = """
            <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01"
              xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
              <Cube><Cube time="2026-10-27"><Cube currency="USD" rate="1.2" />
                <Cube currency="INR" rate="108.0" /></Cube></Cube>
            </gesmes:Envelope>
            """;

        var reference = EcbReferenceRateParser.ParseUsdInr(xml);

        Assert.NotNull(reference);
        Assert.Equal(new DateOnly(2026, 10, 27), reference.Date);
        Assert.Equal(90m, reference.RupeesPerDollar);
    }

    [Fact]
    public void MissingOrInvalidRateIsNotPresentedAsAQuote()
    {
        const string xml = """
            <Envelope><Cube><Cube time="2026-10-27">
              <Cube currency="USD" rate="0" /><Cube currency="INR" rate="108" />
            </Cube></Cube></Envelope>
            """;

        Assert.Null(EcbReferenceRateParser.ParseUsdInr(xml));
    }

    [Fact]
    public void DtdIsRejected()
    {
        Assert.ThrowsAny<Exception>(() => EcbReferenceRateParser.ParseUsdInr(
            "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><x>&e;</x>"));
    }
}
