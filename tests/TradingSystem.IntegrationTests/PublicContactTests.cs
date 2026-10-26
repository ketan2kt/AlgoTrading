using System.Net;
using System.Text;
using TradingSystem.Api.PublicContact;
using TradingSystem.Domain.Trading;

namespace TradingSystem.IntegrationTests;

public sealed class PublicContactTests
{
    private static readonly PublicContactOptions Ready = new()
    {
        RecaptchaSiteKey = "public-site-key", RecaptchaProjectId = "test-project", RecaptchaApiKey = "private-api-key",
        ExpectedHostname = "sarthico.com", RecipientEmail = "recipient@example.com",
        SmtpHost = "smtp.example.com", SmtpUsername = "sender", SmtpPassword = "private-password",
        SmtpFromEmail = "sender@example.com"
    };

    [Fact]
    public void ContactFormFailsClosedUntilEmailAndCaptchaAreConfigured()
    {
        Assert.False(new PublicContactOptions().IsReady);
        Assert.True(Ready.IsReady);
        Assert.False(new PublicContactOptions
        {
            RecaptchaSiteKey = Ready.RecaptchaSiteKey,
            RecipientEmail = Ready.RecipientEmail,
            SmtpHost = Ready.SmtpHost
        }.IsReady);
    }

    [Theory]
    [InlineData(true, "sarthico.com", true)]
    [InlineData(true, "attacker.example", false)]
    [InlineData(false, "sarthico.com", false)]
    public async Task CaptchaRequiresValidTokenAndExpectedHost(bool valid, string host, bool expected)
    {
        using var client = new HttpClient(new StubHandler($"{{\"tokenProperties\":{{\"valid\":{valid.ToString().ToLowerInvariant()},\"hostname\":\"{host}\"}}}}"));
        var verifier = new RecaptchaVerifier(client, Ready);
        Assert.Equal(expected, await verifier.VerifyAsync("one-time-token", null, "Unit test", CancellationToken.None));
    }

    [Fact]
    public void EnquiryRecordsDeliveryLifecycleWithoutStoringCaptchaToken()
    {
        var now = DateTimeOffset.UtcNow;
        var inquiry = new ContactInquiry("Ketan", "ketan@example.com", null, "AI applications", "Please contact me about an application.", now);
        Assert.Equal("Pending", inquiry.DeliveryStatus);
        Assert.Equal(0, inquiry.DeliveryAttempts);
        inquiry.MarkSending(now.AddMinutes(5));
        Assert.Equal("Sending", inquiry.DeliveryStatus);
        Assert.Equal(1, inquiry.DeliveryAttempts);
        inquiry.MarkDelivered(now.AddMinutes(1));
        Assert.Equal("Delivered", inquiry.DeliveryStatus);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://recaptchaenterprise.googleapis.com/v1/projects/test-project/assessments", request.RequestUri?.ToString());
            Assert.Equal("private-api-key", request.Headers.GetValues("X-Goog-Api-Key").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
