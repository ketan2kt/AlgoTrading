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
        var local = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
        var time = TimeOnly.FromDateTime(local.DateTime);
        if (local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
            time >= new TimeOnly(9, 0) && time <= new TimeOnly(15, 45))
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.Result = new ConflictObjectResult("Historical archive work runs outside market hours to protect trading-session resources.");
            return Task.CompletedTask;
        }
        return next();
    }
}
