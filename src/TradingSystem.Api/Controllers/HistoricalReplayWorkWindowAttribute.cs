using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TradingSystem.Api.Controllers;

// Applies to archive coverage/import/report work as well as comparisons:
// even a read-only full-universe scan must not compete with the trading loop.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class HistoricalReplayWorkWindowAttribute : Attribute, IAsyncResourceFilter
{
    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        if (!IsAllowed(clock.GetUtcNow()))
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.Result = new ConflictObjectResult("Historical archive work runs outside market hours to protect trading-session resources.");
            return Task.CompletedTask;
        }
        return next();
    }

    internal static bool IsAllowed(DateTimeOffset utc) => AvailableTime(utc, TimeSpan.FromMinutes(1)) > TimeSpan.Zero;

    internal static TimeSpan AvailableTime(DateTimeOffset utc, TimeSpan maximum)
    {
        var local = utc.ToOffset(TimeSpan.FromHours(5.5));
        var time = local.TimeOfDay;
        var morning = TimeSpan.FromHours(9);
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || time > new TimeSpan(15, 45, 0)) return maximum;
        if (time >= morning) return TimeSpan.Zero;
        return TimeSpan.FromTicks(Math.Min(maximum.Ticks, (morning - time).Ticks));
    }
}
