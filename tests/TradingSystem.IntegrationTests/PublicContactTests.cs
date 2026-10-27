using System.Net;
using System.Text;
using System.Text.Json;
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
    [InlineData(true, "sarthico.com", "CONTACT_SUBMIT", 0.9, true)]
    [InlineData(true, "attacker.example", "CONTACT_SUBMIT", 0.9, false)]
    [InlineData(false, "sarthico.com", "CONTACT_SUBMIT", 0.9, false)]
    [InlineData(true, "sarthico.com", "OTHER_ACTION", 0.9, false)]
    [InlineData(true, "sarthico.com", "CONTACT_SUBMIT", 0.2, false)]
    public async Task CaptchaRequiresValidTokenExpectedHostActionAndScore(bool valid, string host, string action,
        double score, bool expected)
    {
        var body = JsonSerializer.Serialize(new
        {
            tokenProperties = new { valid, hostname = host, action },
            riskAnalysis = new { score }
        });
        using var client = new HttpClient(new StubHandler(body));
        var verifier = new RecaptchaVerifier(client, Ready);
        Assert.Equal(expected, await verifier.VerifyAsync("one-time-token", null, "Unit test", CancellationToken.None));
    }

    [Fact]
    public async Task CaptchaAcceptsV3TokensLongerThanTheOldCheckboxLimitWithinTheBound()
    {
        const int tokenLength = 3_000;
        var body = JsonSerializer.Serialize(new
        {
            tokenProperties = new { valid = true, hostname = "sarthico.com", action = "CONTACT_SUBMIT" },
            riskAnalysis = new { score = 0.9 }
        });
        using var client = new HttpClient(new StubHandler(body, tokenLength));
        var verifier = new RecaptchaVerifier(client, Ready);

        Assert.True(await verifier.VerifyAsync(new string('x', tokenLength), null, "Unit test", CancellationToken.None));
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

    private sealed class StubHandler(string body, int expectedTokenLength = 14) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://recaptchaenterprise.googleapis.com/v1/projects/test-project/assessments", request.RequestUri?.ToString());
            Assert.Equal("private-api-key", request.Headers.GetValues("X-Goog-Api-Key").Single());
            using var requestBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("CONTACT_SUBMIT", requestBody.RootElement.GetProperty("event").GetProperty("expectedAction").GetString());
            Assert.Equal(expectedTokenLength, requestBody.RootElement.GetProperty("event").GetProperty("token").GetString()?.Length);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
