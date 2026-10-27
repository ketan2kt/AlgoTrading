using System.Text.Json;
using System.Net.Http.Json;

namespace TradingSystem.Api.PublicContact;

public sealed class RecaptchaVerifier(HttpClient httpClient, PublicContactOptions options)
{
    private const string ExpectedAction = "CONTACT_SUBMIT";

    public async Task<bool> VerifyAsync(string token, string? remoteIp, string? userAgent, CancellationToken cancellationToken)
    {
        if (!options.IsReady || string.IsNullOrWhiteSpace(token) || token.Length > PublicContactOptions.MaximumRecaptchaTokenLength)
            return false;
        var url = $"https://recaptchaenterprise.googleapis.com/v1/projects/{Uri.EscapeDataString(options.RecaptchaProjectId)}/assessments";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", options.RecaptchaApiKey);
        request.Content = JsonContent.Create(new
        {
            @event = new
            {
                token,
                siteKey = options.RecaptchaSiteKey,
                expectedAction = ExpectedAction,
                userIpAddress = remoteIp,
                userAgent
            }
        });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return false;
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var result = json.RootElement;
        return result.TryGetProperty("tokenProperties", out var properties) &&
               properties.TryGetProperty("valid", out var valid) && valid.GetBoolean() &&
               properties.TryGetProperty("hostname", out var hostname) &&
               string.Equals(hostname.GetString(), options.ExpectedHostname, StringComparison.OrdinalIgnoreCase) &&
               properties.TryGetProperty("action", out var action) &&
               string.Equals(action.GetString(), ExpectedAction, StringComparison.Ordinal) &&
               result.TryGetProperty("riskAnalysis", out var risk) &&
               risk.TryGetProperty("score", out var score) && score.TryGetDouble(out var value) &&
               value >= options.RecaptchaMinimumScore;
    }
}
