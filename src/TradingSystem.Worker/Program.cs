using System.Globalization;
using Serilog;
using TradingSystem.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog(logger => logger
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
