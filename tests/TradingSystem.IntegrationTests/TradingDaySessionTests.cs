using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using TradingSystem.Api.Security;

namespace TradingSystem.IntegrationTests;

public sealed class TradingDaySessionTests
{
    [Theory]
    [InlineData("2026-11-05T03:30:00Z", "2026-11-05T18:30:00Z")]
    [InlineData("2026-11-05T18:29:59Z", "2026-11-05T18:30:00Z")]
    [InlineData("2026-11-05T18:30:00Z", "2026-11-06T18:30:00Z")]
    [InlineData("2026-12-31T20:00:00Z", "2027-01-01T18:30:00Z")]
    public void SessionExpiresAtNextIstMidnight(string login, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), TradingDaySession.ExpiresAt(DateTimeOffset.Parse(login)));

    [Fact]
    public async Task SignInPersistsAcrossBrowserRestartsWithoutSlidingIntoAnotherDay()
    {
        var now = DateTimeOffset.Parse("2026-11-05T04:00:00Z");
        var options = new CookieAuthenticationOptions { TimeProvider = new FixedClock(now) };
        var properties = new AuthenticationProperties { IsPersistent = false, ExpiresUtc = now.AddMinutes(30) };
        var context = new CookieSigningInContext(new DefaultHttpContext(),
            new AuthenticationScheme("test", null, typeof(CookieAuthenticationHandler)), options,
            new ClaimsPrincipal(new ClaimsIdentity()), properties, new CookieOptions());

        await TradingDaySession.Apply(context);

        Assert.True(properties.IsPersistent);
        Assert.False(properties.AllowRefresh);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T18:30:00Z"), properties.ExpiresUtc);
        Assert.True(properties.ExpiresUtc > now.AddHours(8));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
