using System.Net;
using System.Text.Json;
using TradingSystem.Application.MarketData;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class WorkspaceHolidayCalendarTests
{
    private const string Calendar = """
        {"status":"success","data":[
          {"date":"2026-11-24","holiday_type":"TRADING_HOLIDAY","closed_exchanges":["NSE","BSE"],"open_exchanges":[{"exchange":"MCX"}]},
          {"date":"2026-11-26","holiday_type":"TRADING_HOLIDAY","closed_exchanges":["NSE","BSE"],"open_exchanges":[]},
          {"date":"2026-11-27","holiday_type":"TRADING_HOLIDAY","closed_exchanges":["NSE","BSE"],"open_exchanges":[]},
          {"date":"2026-11-28","holiday_type":"SPECIAL_TIMING","closed_exchanges":[],"open_exchanges":[{"exchange":"NSE"}]},
          {"date":"2026-11-30","holiday_type":"SETTLEMENT_HOLIDAY","closed_exchanges":["NSE"],"open_exchanges":[]}
        ]}
        """;

    [Theory]
    [InlineData("NSE", 25349.65)]
    [InlineData("NSE", 62609.20)]
    [InlineData("BSE", 82435.99)]
    public async Task HolidayStaleCandlesDoNotDisplaceCompletedClose(string exchange, decimal close)
    {
        var handler = new Handler();
        var calendar = new WorkspaceHolidayCalendar(new Factory(handler), new Clock());
        var holidays = await calendar.ClosedDatesAsync(exchange, new(2026, 11, 11), new(2026, 11, 25), default);
        var bars = new[] { Bar(23, 15, 29, close), Bar(24, 15, 11, close), Bar(25, 10, 0, close + 100) };
        Assert.Null(WorkspacePreviousClosePolicy.Read(bars, new(2026, 11, 25), new(15, 30)));
        var actual = WorkspacePreviousClosePolicy.Read(bars, new(2026, 11, 25), new(15, 30), holidays);
        Assert.Equal(close, actual!.Price);
        Assert.Equal(new DateOnly(2026, 11, 23), actual.SessionDate);
        await calendar.ClosedDatesAsync("BSE", new(2026, 11, 11), new(2026, 11, 25), default);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConsecutiveHolidaysSkipButMissingRealTradingCloseDoesNot()
    {
        var calendar = new WorkspaceHolidayCalendar(new Factory(new Handler()), new Clock());
        var holidays = await calendar.ClosedDatesAsync("NSE", new(2026, 11, 25), new(2026, 11, 30), default);
        var bars = new[] { Bar(25, 15, 29, 100), Bar(26, 15, 29, 900), Bar(27, 15, 11, 999) };
        Assert.Equal(100, WorkspacePreviousClosePolicy.Read(bars, new(2026, 11, 30), new(15, 30), holidays)!.Price);
        Assert.Null(WorkspacePreviousClosePolicy.Read([..bars, Bar(30, 15, 28, 120)], new(2026, 12, 1), new(15, 30), holidays));
        Assert.DoesNotContain(new DateOnly(2026, 11, 28), holidays);
        Assert.DoesNotContain(new DateOnly(2026, 11, 30), holidays);
    }

    [Fact]
    public async Task CalendarFailureIsCachedAndRetainsPreviouslyVerifiedDates()
    {
        var clock = new Clock();
        var handler = new Handler();
        var calendar = new WorkspaceHolidayCalendar(new Factory(handler), clock);
        var first = await calendar.ClosedDatesAsync("BSE", new(2026, 11, 20), new(2026, 11, 25), default);
        clock.Now = clock.Now.AddHours(13);
        handler.Fail = true;
        Assert.Equal(first, await calendar.ClosedDatesAsync("BSE", new(2026, 11, 20), new(2026, 11, 25), default));
        await calendar.ClosedDatesAsync("NSE", new(2026, 11, 20), new(2026, 11, 25), default);
        Assert.Equal(2, handler.Calls);
        var empty = new WorkspaceHolidayCalendar(new Factory(handler), clock);
        Assert.Empty(await empty.ClosedDatesAsync("BSE", new(2026, 11, 20), new(2026, 11, 25), default));
    }

    [Fact]
    public async Task JanuaryUsesCachedDateLookupForPreviousDecember()
    {
        var clock = new Clock { Now = new(2027, 1, 1, 5, 0, 0, TimeSpan.Zero) };
        var handler = new Handler
        {
            Json = """{"status":"success","data":[{"date":"2026-12-31","holiday_type":"TRADING_HOLIDAY","closed_exchanges":["NSE"],"open_exchanges":[]}]}"""
        };
        var calendar = new WorkspaceHolidayCalendar(new Factory(handler), clock);
        Assert.Contains(new DateOnly(2026, 12, 31),
            await calendar.ClosedDatesAsync("NSE", new(2026, 12, 31), new(2027, 1, 1), default));
        await calendar.ClosedDatesAsync("NSE", new(2026, 12, 31), new(2027, 1, 1), default);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("/2026-12-31", handler.Paths);
    }

    [Fact]
    public void ExplicitOpenExchangeIsNotTreatedAsClosed()
    {
        using var json = JsonDocument.Parse("""{"status":"success","data":[{"date":"2026-11-24","holiday_type":"TRADING_HOLIDAY","closed_exchanges":["NSE","BSE"],"open_exchanges":[{"exchange":"BSE"}]}]}""");
        var days = WorkspaceHolidayCalendar.Parse(json.RootElement);
        Assert.Contains("NSE", days[new(2026, 11, 24)]);
        Assert.DoesNotContain("BSE", days[new(2026, 11, 24)]);
    }

    private static WorkspaceCandle Bar(int day, int hour, int minute, decimal close) =>
        new(new DateTimeOffset(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(5.5)), 60, close, close, close, close, 0, true);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 25, 5, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public bool Fail;
        public string Json = Calendar;
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Paths.Add(request.RequestUri!.AbsolutePath.Replace("/v2/market/holidays", "", StringComparison.Ordinal));
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent(Json) });
        }
    }
}
