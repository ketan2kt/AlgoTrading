using TradingSystem.Infrastructure.Broker.Groww;

namespace TradingSystem.ContractTests;

public sealed class GrowwTotpGeneratorTests
{
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    public void GenerateMatchesRfc6238Sha1VectorsAtSixDigits(long unixSeconds, string expected)
    {
        var result = GrowwTotpGenerator.Generate(
            RfcSecret,
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GenerateRejectsInvalidBase32Secret()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            GrowwTotpGenerator.Generate("NOT-BASE32", DateTimeOffset.UnixEpoch));

        Assert.Contains("Base32", exception.Message, StringComparison.Ordinal);
    }
}
