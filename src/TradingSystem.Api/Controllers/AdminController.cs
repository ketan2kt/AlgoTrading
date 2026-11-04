using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Identity;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/admin")]
public sealed class AdminController(
    UserManager<ApplicationUser> users,
    TradingDbContext database,
    TimeProvider clock) : ControllerBase
{
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers([FromQuery] int skip = 0, [FromQuery] int take = 100,
        [FromQuery, MaxLength(120)] string? search = null, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 100);
        var query = users.Users.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var pending = await query.CountAsync(user => !user.IsActive || !user.EmailConfirmed, cancellationToken);
        var term = search?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(term))
            query = query.Where(u => (u.NormalizedEmail != null && u.NormalizedEmail.Contains(term)) ||
                (u.NormalizedUserName != null && u.NormalizedUserName.Contains(term)) ||
                database.UserRoles.Any(ur => ur.UserId == u.Id && database.Roles.Any(r =>
                    r.Id == ur.RoleId && r.NormalizedName != null && r.NormalizedName.Contains(term))));
        var matched = string.IsNullOrEmpty(term) ? total : await query.CountAsync(cancellationToken);
        var page = await query.OrderByDescending(user => user.CreatedAtUtc)
            .ThenBy(user => user.UserName)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var items = new List<AdminUserDto>(page.Count);
        foreach (var user in page)
        {
            items.Add(new AdminUserDto(user.Id, user.UserName ?? string.Empty, user.Email ?? string.Empty,
                user.CreatedAtUtc, user.IsActive, user.EmailConfirmed, await users.GetRolesAsync(user)));
        }

        return Ok(new AdminUsersPage(total, pending, items, matched));
    }

    [HttpGet("contact-inquiries")]
    public async Task<IActionResult> GetContactInquiries([FromQuery] int skip = 0, [FromQuery] int take = 100,
        [FromQuery, MaxLength(120)] string? search = null, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 100);
        var query = database.ContactInquiries.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var term = search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(term))
            query = query.Where(i => i.Name.ToLower().Contains(term) || i.Email.ToLower().Contains(term) ||
                (i.Company != null && i.Company.ToLower().Contains(term)) || i.Service.ToLower().Contains(term) ||
                i.Message.ToLower().Contains(term));
        var matched = string.IsNullOrEmpty(term) ? total : await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(inquiry => inquiry.CreatedAtUtc).ThenBy(inquiry => inquiry.Id)
            .Skip(skip)
            .Take(take)
            .Select(inquiry => new ContactInquiryDto(inquiry.Id, inquiry.Name, inquiry.Email, inquiry.Company,
                inquiry.Service, inquiry.Message, inquiry.DeliveryStatus, inquiry.DeliveryAttempts,
                inquiry.CreatedAtUtc, inquiry.DeliveredAtUtc))
            .ToListAsync(cancellationToken);

        return Ok(new AdminPage<ContactInquiryDto>(total, items, matched));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("users")]
    public async Task<IActionResult> CreatePendingUser(CreateAdminUserRequest request,
        CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
            return Conflict(new { message = "A user with this email already exists." });

        var now = clock.GetUtcNow();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = false,
            IsActive = false,
            CreatedAtUtc = now
        };

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var result = await users.CreateAsync(user);
        if (!result.Succeeded)
        {
            var duplicate = result.Errors.Any(error => error.Code.Contains("Duplicate", StringComparison.Ordinal));
            return duplicate
                ? Conflict(new { message = "A user with this email already exists." })
                : BadRequest(new { message = "The account could not be created. Check the email and try again." });
        }

        database.AuditLogs.Add(new AuditLog(
            Guid.NewGuid(),
            User.Identity?.Name ?? "administrator",
            "PendingTradingUserCreated",
            nameof(ApplicationUser),
            user.Id.ToString(),
            "Created from the Sarthico admin panel; disabled pending access approval.",
            "{}",
            JsonSerializer.Serialize(new { user.Email, user.IsActive, user.EmailConfirmed }),
            HttpContext.TraceIdentifier,
            now));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created,
            new AdminUserDto(user.Id, user.UserName ?? email, email, user.CreatedAtUtc, user.IsActive,
                user.EmailConfirmed, []));
    }
}

public sealed record AdminPage<T>(int Total, IReadOnlyList<T> Items, int Matched);
public sealed record AdminUsersPage(int Total, int Pending, IReadOnlyList<AdminUserDto> Items, int Matched);

public sealed record AdminUserDto(Guid Id, string Username, string Email, DateTimeOffset CreatedAtUtc,
    bool IsActive, bool EmailConfirmed, IList<string> Roles);

public sealed record ContactInquiryDto(Guid Id, string Name, string Email, string? Company, string Service,
    string Message, string DeliveryStatus, int DeliveryAttempts, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeliveredAtUtc);

public sealed class CreateAdminUserRequest
{
    [Required, EmailAddress, StringLength(254)]
    public required string Email { get; init; }
}
