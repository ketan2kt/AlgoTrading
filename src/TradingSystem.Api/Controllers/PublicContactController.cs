using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradingSystem.Api.PublicContact;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/public/contact")]
public sealed class PublicContactController(
    PublicContactOptions options,
    RecaptchaVerifier captcha,
    TradingDbContext database,
    TimeProvider clock) : ControllerBase
{
    [HttpGet("config")]
    public IActionResult Config() => Ok(new { enabled = options.IsReady, siteKey = options.IsReady ? options.RecaptchaSiteKey : null });

    [HttpPost]
    [EnableRateLimiting("public-contact")]
    [RequestSizeLimit(12_000)]
    public async Task<IActionResult> Submit([FromBody] ContactRequest request, CancellationToken cancellationToken)
    {
        if (!options.IsReady) return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { message = "Contact form is temporarily unavailable." });
        if (!string.IsNullOrEmpty(request.Website)) return Accepted();
        if (!Valid(request)) return BadRequest(new { message = "Please check the form fields and try again." });

        bool verified;
        try
        {
            verified = await captcha.VerifyAsync(request.RecaptchaToken, HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(), cancellationToken);
        }
        catch (HttpRequestException) { return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Verification is unavailable. Please retry." }); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Verification timed out. Please retry." }); }
        catch (JsonException)
        { return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Verification is unavailable. Please retry." }); }
        if (!verified) return BadRequest(new { message = "Verification expired or failed. Please try again." });

        database.ContactInquiries.Add(new ContactInquiry(
            request.Name.Trim(), request.Email.Trim(), request.Company?.Trim(), request.Service,
            request.Message.Trim(), clock.GetUtcNow()));
        await database.SaveChangesAsync(cancellationToken);
        return Accepted(new { message = "Thanks. Your enquiry has been received." });
    }

    internal static bool Valid(ContactRequest request) =>
        !string.IsNullOrWhiteSpace(request.Name) && request.Name.Trim().Length <= 120 &&
        !string.IsNullOrWhiteSpace(request.Email) && request.Email.Length <= 254 &&
        new EmailAddressAttribute().IsValid(request.Email.Trim()) &&
        (request.Company is null || request.Company.Length <= 120) &&
        request.Service is "Web development" or "Application development" or "AI applications" or "Other" &&
        !string.IsNullOrWhiteSpace(request.Message) && request.Message.Trim().Length is >= 10 and <= 4000 &&
        !string.IsNullOrWhiteSpace(request.RecaptchaToken) && request.RecaptchaToken.Length <= 2048;

    public sealed record ContactRequest(string Name, string Email, string? Company, string Service,
        string Message, string RecaptchaToken, string? Website);
}
