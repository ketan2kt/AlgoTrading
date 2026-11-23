using System.Security.Cryptography;
using System.Text.Encodings.Web;
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
    [AllowAnonymous]
    [EnableRateLimiting("authentication")]
    public IActionResult Callback(string? code, string? state)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code) || state.Length > 4096 || code.Length > 2048)
            return BadRequest("Upstox authorization was not completed.");
        string unpacked;
        try { unpacked = Protector.Unprotect(state); }
        catch (CryptographicException) { return BadRequest("Upstox authorization expired. Connect again from Controls."); }
        if (!Request.Cookies.TryGetValue(StateCookie, out var nonce) || nonce.Length != 64 ||
            !unpacked.EndsWith("|" + nonce, StringComparison.Ordinal))
            return BadRequest("Upstox authorization does not match this browser session.");

        // The administrator cookie stays SameSite=Strict. The cross-site broker
        // return can only render this inert page, never exchange/store a token.
        // A user-initiated same-site submission sends the administrator cookie;
        // Complete rechecks its identity, protected state and browser nonce.
        return Content("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Complete Upstox connection</title>" +
            "<h1>Complete Upstox connection</h1><p>Verify read-only account access. This does not place orders or enable live entries.</p>" +
            "<form method=\"get\" action=\"/api/upstox-execution/complete\">" +
            "<input type=\"hidden\" name=\"code\" value=\"" + HtmlEncoder.Default.Encode(code) + "\">" +
            "<input type=\"hidden\" name=\"state\" value=\"" + HtmlEncoder.Default.Encode(state) + "\">" +
            "<button type=\"submit\">Complete secure connection</button></form></html>", "text/html");
    }

    [HttpGet("complete")]
    [EnableRateLimiting("authentication")]
    public async Task<IActionResult> Complete(string? code, string? state, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code) || state.Length > 4096 || code.Length > 2048)
            return BadRequest("Upstox authorization was not completed.");
        string unpacked;
        try { unpacked = Protector.Unprotect(state); }
        catch (CryptographicException) { return BadRequest("Upstox authorization expired. Connect again from Controls."); }
        if (!Request.Cookies.TryGetValue(StateCookie, out var nonce) ||
            unpacked != User.Identity!.Name + "|" + nonce)
            return BadRequest("Upstox authorization does not match this administrator session.");
        Response.Cookies.Delete(StateCookie, new CookieOptions { Secure = true, HttpOnly = true, Path = "/" });
        try { await authorization.CompleteAsync(code, User.Identity!.Name!, token); }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or System.Text.Json.JsonException or InvalidDataException)
        { return BadRequest("Upstox trading authorization could not be verified. No live switches were changed. Reconnect from Controls."); }
        return Redirect("/nifty?executionConnected=Upstox");
    }
}
