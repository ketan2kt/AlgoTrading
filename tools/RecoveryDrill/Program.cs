using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.Json;
using TradingSystem.Application.Broker;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure;
using TradingSystem.Infrastructure.Persistence;

// Isolated operations drill: synthetic state only, no web host, scheduler or broker HTTP calls.
if (args.Length != 4 || args[0] is not ("seed" or "verify" or "verify-missing-keys") ||
    !int.TryParse(args[1], out var port) || port is < 1024 or > 65535 ||
    args[2] is not ("recovery_drill_source" or "recovery_drill_restored"))
    throw new ArgumentException("Use seed|verify|verify-missing-keys <local-port> recovery_drill_source|recovery_drill_restored <key-directory>.");

var connection = new NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1", Port = port, Database = args[2], Username = "recovery_drill",
    Timeout = 10, CommandTimeout = 60, Pooling = false
}.ConnectionString;
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:TradingDatabase"] = connection,
    ["Trading:Mode"] = "Paper",
    ["LiveExecution:BuildEnabled"] = "false",
    ["IdentityBootstrap:Enabled"] = "false",
    ["MarketData:HistoricalReplayFeed:Enabled"] = "false"
}).Build();
var services = new ServiceCollection();
services.AddLogging();
services.AddTradingSystemInfrastructure(configuration);
services.AddDataProtection().SetApplicationName("Sarthi-Isolated-Recovery-Drill")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(args[3])));
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
var vault = scope.ServiceProvider.GetRequiredService<IGrowwTokenVault>();
const string syntheticToken = "synthetic-recovery-fixture-not-a-real-broker-token";
var instrument = Guid.Parse("0c79cd28-bd09-4b0e-a522-1b9d15ff94ee");

if (args[0] == "seed")
{
    await db.Database.MigrateAsync();
    if (await db.ApplicationSettings.AnyAsync() || await db.PaperBrokerEvents.AnyAsync())
        throw new InvalidOperationException("Drill refuses an already-populated database.");
    var id = Guid.NewGuid();
    db.ApplicationSettings.Add(new ApplicationSetting(id, TradingMode.Paper, "RecoveryDrill.Setting",
        "{\"value\":3}", DateTimeOffset.UtcNow));
    db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), "recovery-drill", "Created", "ApplicationSetting",
        id.ToString(), "Synthetic recovery evidence", "{}", "{\"value\":3}",
        "recovery-drill", DateTimeOffset.UtcNow));
    await db.SaveChangesAsync();
    await vault.StoreAsync(syntheticToken, "recovery-drill", CancellationToken.None);
    var broker = provider.GetRequiredService<IBrokerGateway>();
    await broker.SubmitAsync(new("recovery-drill-order", instrument, Direction.Buy, 7, 100m, 3), CancellationToken.None);
    await provider.GetRequiredService<IPaperBrokerControl>().ProcessNextFillAsync("recovery-drill-order", CancellationToken.None);
    Console.WriteLine("Seeded current migrations, setting/audit, encrypted synthetic token and partial paper fill.");
    return;
}

if (args[0] == "verify-missing-keys")
{
    try { await vault.GetValidTokenAsync(CancellationToken.None); }
    catch (InvalidOperationException error) when (error.Message ==
        "The protected Groww token cannot be decrypted; replace it through the administrator screen.")
    {
        Console.WriteLine("PASS: missing restored key ring blocks credential decryption.");
        return;
    }
    throw new InvalidOperationException("Missing-key negative control unexpectedly decrypted the fixture.");
}

if ((await db.Database.GetPendingMigrationsAsync()).Any() ||
    await db.ApplicationSettings.CountAsync() != 1 || await db.AuditLogs.CountAsync() != 2 ||
    await db.PaperBrokerEvents.CountAsync() != 2 || await db.BrokerAccessTokenSecrets.CountAsync() != 1)
    throw new InvalidDataException("Restored migrations or critical row counts differ.");
using var settingJson = JsonDocument.Parse((await db.ApplicationSettings.SingleAsync()).ValueJson);
if (settingJson.RootElement.EnumerateObject().Count() != 1 ||
    settingJson.RootElement.GetProperty("value").GetInt32() != 3)
    throw new InvalidDataException("Restored setting differs.");
var secret = await db.BrokerAccessTokenSecrets.SingleAsync();
if (secret.ProtectedValue.Contains(syntheticToken, StringComparison.Ordinal) ||
    await vault.GetValidTokenAsync(CancellationToken.None) != syntheticToken)
    throw new InvalidDataException("Restored key-ring/database pair failed encrypted credential verification.");
var restoredBroker = provider.GetRequiredService<IBrokerGateway>();
var restoredOrder = await restoredBroker.GetOrderAsync("recovery-drill-order", CancellationToken.None);
var positions = await restoredBroker.GetPositionsAsync(CancellationToken.None);
if (restoredOrder?.State != OrderState.PartiallyFilled || restoredOrder.FilledQuantity != 3 ||
    positions.Count != 1 || positions[0].InstrumentId != instrument || positions[0].Quantity != 3)
    throw new InvalidDataException("Restored paper journal did not reconstruct the partial fill.");
// A repeated request after restore must not create another order or journal event.
await restoredBroker.SubmitAsync(new("recovery-drill-order", instrument, Direction.Buy, 7, 100m, 3), CancellationToken.None);
if (await db.PaperBrokerEvents.CountAsync() != 2)
    throw new InvalidDataException("Restored duplicate-request protection failed.");
Console.WriteLine("PASS: migrations, exact setting, audit, encrypted token/key-ring, partial position and duplicate-order prevention restored.");
