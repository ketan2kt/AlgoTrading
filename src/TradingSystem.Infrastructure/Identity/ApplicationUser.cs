using Microsoft.AspNetCore.Identity;

namespace TradingSystem.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAtUtc { get; set; }

    public bool IsActive { get; set; } = true;
}

