using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using TradingSystem.Infrastructure.Identity;

namespace TradingSystem.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthenticationController(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private static readonly string[] AdministratorRoles = ["Administrator"];

    [AllowAnonymous]
    [EnableRateLimiting("authentication")]
    [ValidateAntiForgeryToken]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var loginName = request.Username.Trim();
        var user = loginName.Contains('@', StringComparison.Ordinal)
            ? await userManager.FindByEmailAsync(loginName)
            : await userManager.FindByNameAsync(loginName);
        if (user?.UserName is null)
        {
            return Unauthorized();
        }

        var result = await signInManager.PasswordSignInAsync(
            user.UserName,
            request.Password,
            isPersistent: false,
            lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            return Unauthorized();
        }

        if (!await userManager.IsInRoleAsync(user, AdministratorRoles[0]))
        {
            await signInManager.SignOutAsync();
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

    [Authorize(Roles = "Administrator")]
    [HttpGet("me")]
    public IActionResult Me() =>
        Ok(new
        {
            username = User.Identity?.Name,
            roles = User.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray()
        });
}

public sealed record LoginRequest(string Username, string Password);
