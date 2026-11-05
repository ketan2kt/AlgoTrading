using Microsoft.AspNetCore.Authentication.Cookies;

namespace TradingSystem.Api.Security;

public static class TradingDaySession
{
    public static DateTimeOffset ExpiresAt(DateTimeOffset now)
    {
        var ist = TimeSpan.FromMinutes(330);
        return new DateTimeOffset(now.ToOffset(ist).Date.AddDays(1), ist).ToUniversalTime();
    }

    public static Task Apply(CookieSigningInContext context)
    {
        context.Properties.IsPersistent = true;
        context.Properties.AllowRefresh = false;
        context.Properties.ExpiresUtc = ExpiresAt(context.Options.TimeProvider!.GetUtcNow());
        return Task.CompletedTask;
    }
}
