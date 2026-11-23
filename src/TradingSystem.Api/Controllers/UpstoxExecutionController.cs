using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradingSystem.Application.Broker;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/upstox-execution")]
public sealed class UpstoxExecutionController(IUpstoxExecutionAuthorization authorization,
    IDataProtectionProvider protection) : ControllerBase
{
    private const string StateCookie = "__Host-upstox-execution-state";
    private ITimeLimitedDataProtector Protector => protection.CreateProtector("UpstoxExecutionOAuthState.v1").ToTimeLimitedDataProtector();

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await authorization.GetStatusAsync(token));
    }

    [HttpPost("connect")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("authentication")]
    public IActionResult Connect()
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var state = Protector.Protect(User.Identity!.Name + "|" + nonce, TimeSpan.FromMinutes(10));
        var url = authorization.AuthorizationUrl(state);
        Response.Cookies.Append(StateCookie, nonce, new CookieOptions { Secure = true, HttpOnly = true,
            SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromMinutes(10), IsEssential = true });
        Response.Headers.CacheControl = "no-store";
        return Ok(new { url });
    }

    [HttpGet("callback")]
    [EnableRateLimiting("authentication")]
    public async Task<IActionResult> Callback(string? code, string? state, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code) || state.Length > 4096)
            return BadRequest("Upstox authorization was not completed.");
        string unpacked;
        try { unpacked = Protector.Unprotect(state); }
        catch (CryptographicException) { return BadRequest("Upstox authorization expired. Connect again from Controls."); }
        if (!Request.Cookies.TryGetValue(StateCookie, out var nonce) ||
            unpacked != User.Identity!.Name + "|" + nonce)
            return BadRequest("Upstox authorization does not match this administrator session.");
        Response.Cookies.Delete(StateCookie, new CookieOptions { Secure = true, HttpOnly = true, Path = "/" });
        try { await authorization.CompleteAsync(code, User.Identity!.Name!, token); }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or System.Text.Json.JsonException)
        { return BadRequest("Upstox trading authorization could not be verified. No live switches were changed. Reconnect from Controls."); }
        return Redirect("/nifty?executionConnected=Upstox");
    }
}
