using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TradingSystem.Infrastructure.Persistence;

public sealed class TradingDbContextFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("TRADING_SYSTEM_DB")
            ?? "Host=localhost;Database=trading_system;Username=trading_app";
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(TradingDbContext).Assembly.FullName))
            .Options;

        return new TradingDbContext(options, TimeProvider.System);
    }
}

