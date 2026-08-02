namespace TradingSystem.Infrastructure.Persistence;

public sealed class DatabaseInitializationOptions
{
    public const string SectionName = "DatabaseInitialization";

    public bool ApplyMigrations { get; init; }
}
