using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using TradingSystem.Api.Hubs;
using TradingSystem.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddControllersWithViews().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services
    .AddHealthChecks()
    .AddCheck<TradingSystem.Infrastructure.Persistence.TradingDatabaseHealthCheck>(
        "postgresql",
        tags: ["ready", "trading-ready"]);
builder.Services.AddProblemDetails();
var dataProtectionPath = builder.Configuration["DataProtection:KeysPath"];
var keyDirectory = string.IsNullOrWhiteSpace(dataProtectionPath)
    ? Path.Combine(builder.Environment.ContentRootPath, ".data-protection-keys")
    : Path.GetFullPath(dataProtectionPath);
var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName("TradingSystem")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-TradingSystem-Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
builder.Services.AddTradingSystemInfrastructure(builder.Configuration);
builder.Services.AddSingleton<TradingSystem.Application.MarketData.ILiveMarketDataPublisher,
    SignalRLiveMarketDataPublisher>();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "__Host-TradingSystem";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddRateLimiter(options =>
    options.AddFixedWindowLimiter("authentication", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    }));
builder.Services
    .AddHttpClient("resilient-external")
    .AddStandardResilienceHandler();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                       ForwardedHeaders.XForwardedProto
});
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new()
{
    Predicate = registration => registration.Tags.Contains("ready")
}).AllowAnonymous();
app.MapHub<SystemHealthHub>("/hubs/system-health");
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
