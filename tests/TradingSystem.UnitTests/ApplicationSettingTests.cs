using TradingSystem.Domain;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class ApplicationSettingTests
{
    [Fact]
    public void SettingIsModeScopedAndRotatesConcurrencyToken()
    {
        var createdAt = new DateTimeOffset(2026, 8, 2, 3, 30, 0, TimeSpan.Zero);
        var setting = new ApplicationSetting(
            Guid.NewGuid(),
            TradingMode.Paper,
            "Risk.MaximumTradesPerDay",
            """{"value":3}""",
            createdAt);
        var originalToken = setting.ConcurrencyToken;

        setting.ChangeValue("""{"value":2}""", createdAt.AddMinutes(1));

        Assert.Equal(TradingMode.Paper, setting.Mode);
        Assert.Equal("""{"value":2}""", setting.ValueJson);
        Assert.NotEqual(originalToken, setting.ConcurrencyToken);
        Assert.Equal(createdAt.AddMinutes(1), setting.UpdatedAtUtc);
    }

    [Fact]
    public void SettingRejectsEmptyKey()
    {
        Assert.Throws<ArgumentException>(() => new ApplicationSetting(
            Guid.NewGuid(),
            TradingMode.Paper,
            " ",
            "{}",
            DateTimeOffset.UtcNow));
    }
}

