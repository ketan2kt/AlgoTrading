using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradingSystem.Infrastructure.Identity;

namespace TradingSystem.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthenticationController(
    SignInManager<ApplicationUser> signInManager) : ControllerBase
{
    private static readonly string[] AdministratorRoles = ["Administrator"];

    [AllowAnonymous]
    [EnableRateLimiting("authentication")]
    [ValidateAntiForgeryToken]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var result = await signInManager.PasswordSignInAsync(
            request.Username,
            request.Password,
            isPersistent: false,
            lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            return Unauthorized();
        }

        return NoContent();
    }

    [Authorize]
    [ValidateAntiForgeryToken]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() =>
        Ok(new { username = User.Identity?.Name, roles = AdministratorRoles });
}

public sealed record LoginRequest(string Username, string Password);
