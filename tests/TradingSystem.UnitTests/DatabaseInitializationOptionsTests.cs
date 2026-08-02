using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.UnitTests;

public sealed class DatabaseInitializationOptionsTests
{
    [Fact]
    public void ApplyMigrationsIsDisabledByDefault()
    {
        var options = new DatabaseInitializationOptions();

        Assert.False(options.ApplyMigrations);
    }
}
