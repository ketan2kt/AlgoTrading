using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TradingSystem.Api.Controllers;
using TradingSystem.Application.Execution;

namespace TradingSystem.IntegrationTests;

public sealed class HistoricalReplaySecurityTests
{
    [Theory]
    [InlineData(4, 9, 0, false)]
    [InlineData(4, 15, 45, false)]
    [InlineData(4, 16, 0, true)]
    [InlineData(7, 10, 0, true)]
    public async Task HeavyArchiveWorkNeverRunsDuringTheTradingWindow(int day, int hour, int minute, bool allowed)
    {
        using var services = new ServiceCollection().AddSingleton<TimeProvider>(new Clock(new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(5.5))))
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ResourceExecutingContext(action, [], []);
        var called = false;
        await new HistoricalReplayWorkWindowAttribute().OnResourceExecutionAsync(context, () =>
        {
            called = true;
            return Task.FromResult(new ResourceExecutedContext(action, []));
        });
        Assert.Equal(allowed, called);
        if (!allowed) Assert.IsType<ConflictObjectResult>(context.Result);
    }

    [Fact]
    public void CompletedTodayIsUsableAfterCaptureClosesWithoutWaitingAnotherDay()
    {
        var date = new DateOnly(2026, 11, 4);
        var beforeClose = new DateTimeOffset(2026, 11, 4, 15, 30, 0, TimeSpan.FromHours(5.5));
        Assert.False(HistoricalReplaySessionCalendar.IsCompletedDate(date, beforeClose));
        Assert.True(HistoricalReplaySessionCalendar.IsCompletedDate(date, beforeClose.AddMinutes(15)));
        Assert.True(HistoricalReplaySessionCalendar.IsCompletedDate(date.AddDays(-1), beforeClose));
        Assert.False(HistoricalReplaySessionCalendar.IsCompletedDate(date.AddDays(1), beforeClose.AddDays(0).AddHours(3)));
    }

    [Fact]
    public void ReplayRemainsAdministratorOnlyAndComparisonsRequireAntiforgery()
    {
        Assert.Equal("Administrator", Assert.Single(typeof(HistoricalNormalReplayController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>()).Roles);
        Assert.NotEmpty(typeof(HistoricalNormalReplayController).GetMethod(nameof(HistoricalNormalReplayController.Compare))!
            .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        var gapAware = typeof(HistoricalNormalReplayController).GetMethod(nameof(HistoricalNormalReplayController.GapAware))!;
        Assert.NotEmpty(gapAware.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        Assert.NotEmpty(gapAware.GetCustomAttributes(typeof(HistoricalReplayWorkWindowAttribute), true));
        Assert.NotEmpty(typeof(HistoricalReplayController).GetCustomAttributes(typeof(HistoricalReplayWorkWindowAttribute), true));
    }

    private sealed class Clock(DateTimeOffset at) : TimeProvider { public override DateTimeOffset GetUtcNow() => at; }
}
