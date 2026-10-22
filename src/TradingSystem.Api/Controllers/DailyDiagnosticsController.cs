using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Diagnostics;

namespace TradingSystem.Api.Controllers;

public sealed class ReadOnlyDiagnosticsOptions
{
    public const string SectionName = "Diagnostics";
    public string ReadOnlyApiKey { get; init; } = string.Empty;
}

public static class ReadOnlyDiagnosticsKeyValidator
{
    public static bool IsConfigured(string? configuredKey) =>
        !string.IsNullOrWhiteSpace(configuredKey) && configuredKey.Length >= 32;

    public static bool IsAuthorized(string? configuredKey, string? suppliedKey)
    {
        if (!IsConfigured(configuredKey) || string.IsNullOrWhiteSpace(suppliedKey))
            return false;

        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey!));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        return CryptographicOperations.FixedTimeEquals(configuredHash, suppliedHash);
    }
}

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("diagnostics")]
[Route("api/internal/diagnostics/daily")]
public sealed class DailyDiagnosticsController(
    IDailyTradingDiagnosticsReader reader,
    IOptions<ReadOnlyDiagnosticsOptions> options,
    TimeProvider timeProvider) : ControllerBase
{
    private const string ApiKeyHeader = "X-Sarthi-Diagnostics-Key";
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);

    [HttpGet]
    public async Task<ActionResult<DailyTradingDiagnosticsReport>> Get(
        [FromQuery] DateOnly? date,
        [FromQuery] string market = "all",
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var configuredKey = options.Value.ReadOnlyApiKey;
        if (!ReadOnlyDiagnosticsKeyValidator.IsConfigured(configuredKey))
            return NotFound();

        var suppliedKey = Request.Headers[ApiKeyHeader].FirstOrDefault();
        if (!ReadOnlyDiagnosticsKeyValidator.IsAuthorized(configuredKey, suppliedKey))
            return Unauthorized();

        var indiaNow = timeProvider.GetUtcNow().ToOffset(IndiaOffset);
        var reportDate = date ?? DateOnly.FromDateTime(indiaNow.DateTime);
        try
        {
            return Ok(await reader.ReadAsync(reportDate, market, cancellationToken));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            ModelState.AddModelError(nameof(market), exception.Message);
            return ValidationProblem(ModelState);
        }
    }
}
