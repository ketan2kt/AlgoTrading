using System.Net;
using System.Text;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Broker.Groww;

namespace TradingSystem.ContractTests;

public sealed class GrowwExecutionContractTests
{
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
    public async Task ProtectionUsesOfficialFnoOcoContract()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var gateway = CreateGateway(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.Created,
                """{"status":"SUCCESS","payload":{"smart_order_id":"oco-123","smart_order_type":"OCO","status":"ACTIVE"}}""");
        });

        var result = await gateway.CreateProtectionAsync(new BrokerProtectionRequest(
            "OCO-00001", "NIFTY26SEP24500CE", "NSE", 65, 65, 120m, 95m),
            CancellationToken.None);

        Assert.Equal("oco-123", result.BrokerProtectionId);
        Assert.Equal("/v1/order-advance/create", captured!.RequestUri!.AbsolutePath);
        Assert.Contains("\"smart_order_type\":\"OCO\"", body, StringComparison.Ordinal);
        Assert.Contains("\"net_position_quantity\":65", body, StringComparison.Ordinal);
        Assert.Contains("\"transaction_type\":\"SELL\"", body, StringComparison.Ordinal);
        Assert.Contains("\"order_type\":\"SL_M\"", body, StringComparison.Ordinal);
        Assert.Contains("\"product_type\":\"NRML\"", body, StringComparison.Ordinal);
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
