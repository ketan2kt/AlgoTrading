using System.Net;
using System.Text;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Broker.Groww;

namespace TradingSystem.ContractTests;

public sealed class GrowwExecutionContractTests
{
    [Theory]
    [InlineData(true, 401)]
    [InlineData(false, 401)]
    [InlineData(false, 403)]
    public async Task AuthorizationFailureIdentifiesStageWithoutRetryOrSecrets(bool failLookup, int status)
    {
        var calls = new List<HttpMethod>();
        var gateway = CreateGateway(request =>
        {
            calls.Add(request.Method);
            return Task.FromResult(request.Method == HttpMethod.Get && !failLookup
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json((HttpStatusCode)status, "sensitive-broker-body"));
        });

        var error = await Assert.ThrowsAsync<GrowwApiException>(() => gateway.SubmitAsync(
            new BrokerOrderRequest("LIVE-0001", Guid.NewGuid(), Direction.Buy, 65, 100m,
                TradingSymbol: "NIFTY26SEP24500CE", Exchange: "NSE"), CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(failLookup ? 1 : 2, calls.Count);
        Assert.Contains(failLookup ? "GET /v1/order/status/reference/{reference}" : "POST /v1/order/create", error.Message);
        Assert.Contains("route: direct", error.Message);
        Assert.Contains("allowlisted outbound IP", error.Message);
        Assert.DoesNotContain("LIVE-0001", error.Message);
        Assert.DoesNotContain("test-token", error.Message);
        Assert.DoesNotContain("sensitive-broker-body", error.Message);
    }

    [Fact]
    public async Task SubmitChecksReferenceThenUsesOfficialCreateContract()
    {
        var requests = new List<(HttpMethod Method, Uri Uri, string? Body, string? Token)>();
        var gateway = CreateGateway(async request =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method, request.RequestUri!, body,
                request.Headers.Authorization?.Parameter));
            return request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.OK,
                    """{"status":"SUCCESS","payload":{"groww_order_id":"GROW-1","order_status":"OPEN","order_reference_id":"LIVE-0001"}}""");
        });

        var snapshot = await gateway.SubmitAsync(new BrokerOrderRequest(
            "LIVE-0001", Guid.NewGuid(), Direction.Buy, 65, 100m,
            TradingSymbol: "NIFTY26SEP24500CE", Exchange: "NSE"), CancellationToken.None);

        Assert.Equal(OrderState.BrokerAcknowledged, snapshot.State);
        Assert.Equal(2, requests.Count);
        Assert.Equal("/v1/order/status/reference/LIVE-0001", requests[0].Uri.AbsolutePath);
        Assert.Equal("/v1/order/create", requests[1].Uri.AbsolutePath);
        Assert.Equal("test-token", requests[1].Token);
        Assert.Contains("\"segment\":\"FNO\"", requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"order_reference_id\":\"LIVE-0001\"", requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"transaction_type\":\"BUY\"", requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousCreateIsNotRetriedAndRequiresReconciliation()
    {
        var createCalls = 0;
        var lookupCalls = 0;
        var gateway = CreateGateway(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                createCalls++;
                throw new HttpRequestException("connection lost after send");
            }
            lookupCalls++;
            return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        });

        await Assert.ThrowsAsync<BrokerOrderOutcomeUnknownException>(() => gateway.SubmitAsync(
            new BrokerOrderRequest("LIVE-0002", Guid.NewGuid(), Direction.Buy, 65, 100m,
                TradingSymbol: "NIFTY26SEP24500CE", Exchange: "NSE"), CancellationToken.None));

        Assert.Equal(1, createCalls);
        Assert.Equal(2, lookupCalls);
    }

    [Fact]
    public async Task EmptyTradeHistoryDoesNotMakeAnAcceptedOrderDisappear()
    {
        var gateway = CreateGateway(_ => Task.FromResult(Json(HttpStatusCode.NotFound, "{}")));

        Assert.Null(await gateway.GetAverageFillPriceAsync("GROW-1", CancellationToken.None));
    }

    [Fact]
    public async Task InvalidLiveOrderIsRejectedBeforeNetworkAccess()
    {
        var calls = 0;
        var gateway = CreateGateway(_ =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.SubmitAsync(
            new BrokerOrderRequest("short", Guid.NewGuid(), Direction.Buy, 65, 100m,
                TradingSymbol: "NIFTY26SEP24500CE", Exchange: "NSE"), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ProtectionUsesDayStopLimitWithoutAnIndependentProfitTarget()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Body)>();
        var gateway = CreateGateway(async request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Content is null ? null : await request.Content.ReadAsStringAsync()));
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.NotFound, "{}");
            return Json(HttpStatusCode.Created,
                """{"status":"SUCCESS","payload":{"groww_order_id":"STOP-123","order_status":"OPEN"}}""");
        });

        var result = await gateway.CreateProtectionAsync(new BrokerProtectionRequest(
            "STOP-00001", Guid.NewGuid(), "NIFTY26SEP24500CE", "NSE", 65, 95m),
            CancellationToken.None);

        Assert.Equal("STOP-123", result.BrokerProtectionId);
        Assert.Equal(2, requests.Count);
        Assert.Equal("/v1/order/create", requests[1].Path);
        Assert.Contains("\"transaction_type\":\"SELL\"", requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"order_type\":\"SL\"", requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"trigger_price\":95", requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"price\":93.1", requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"validity\":\"DAY\"", requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("target", requests[1].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopModificationUsesOfficialFnoOrderContract()
    {
        string? body = null;
        var gateway = CreateGateway(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK,
                """{"status":"SUCCESS","payload":{"groww_order_id":"STOP-123","order_status":"OPEN"}}""");
        });

        var result = await gateway.ModifyProtectionAsync("STOP-123", 40, 97.50m,
            CancellationToken.None);

        Assert.Equal(40, result.Quantity);
        Assert.Equal(97.50m, result.StopLoss);
        Assert.Contains("\"groww_order_id\":\"STOP-123\"", body, StringComparison.Ordinal);
        Assert.Contains("\"order_type\":\"SL\"", body, StringComparison.Ordinal);
        Assert.Contains("\"quantity\":40", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("REJECTED")]
    [InlineData("COMPLETED")]
    [InlineData("CANCELLED")]
    public async Task StopModificationDoesNotAcceptADeadBrokerOrder(string status)
    {
        var gateway = CreateGateway(_ => Task.FromResult(Json(HttpStatusCode.OK,
            $"{{\"status\":\"SUCCESS\",\"payload\":{{\"groww_order_id\":\"STOP-123\",\"order_status\":\"{status}\"}}}}")));

        await Assert.ThrowsAsync<BrokerOrderOutcomeUnknownException>(() =>
            gateway.ModifyProtectionAsync("STOP-123", 40, 97.50m, CancellationToken.None));
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("REJECTED")]
    public async Task StopCancellationDoesNotClaimSuccessWhileBrokerStillHasAnOrder(string status)
    {
        var gateway = CreateGateway(_ => Task.FromResult(Json(HttpStatusCode.OK,
            $"{{\"status\":\"SUCCESS\",\"payload\":{{\"groww_order_id\":\"STOP-123\",\"order_status\":\"{status}\"}}}}")));

        await Assert.ThrowsAsync<BrokerOrderOutcomeUnknownException>(() =>
            gateway.CancelProtectionAsync("STOP-123", CancellationToken.None));
    }

    private static GrowwBrokerGateway CreateGateway(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var client = new HttpClient(new StubHandler(responder))
            { BaseAddress = new Uri("https://api.groww.in/") };
        return new GrowwBrokerGateway(new StubFactory(client), new StubTokenProvider(), null!,
            TimeProvider.System);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return responder(request);
        }
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubTokenProvider : IGrowwAccessTokenProvider
    {
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult("test-token");
    }
}
