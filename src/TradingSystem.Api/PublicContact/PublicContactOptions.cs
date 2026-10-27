namespace TradingSystem.Api.PublicContact;

public sealed class PublicContactOptions
{
    public string RecaptchaSiteKey { get; init; } = string.Empty;
    public string RecaptchaProjectId { get; init; } = string.Empty;
    public string RecaptchaApiKey { get; init; } = string.Empty;
    public double RecaptchaMinimumScore { get; init; } = 0.5;
    public string ExpectedHostname { get; init; } = "sarthico.com";
    public string RecipientEmail { get; init; } = string.Empty;
    public string SmtpHost { get; init; } = string.Empty;
    public int SmtpPort { get; init; } = 587;
    public string SmtpUsername { get; init; } = string.Empty;
    public string SmtpPassword { get; init; } = string.Empty;
    public string SmtpFromEmail { get; init; } = string.Empty;

    public bool IsReady =>
        !string.IsNullOrWhiteSpace(RecaptchaSiteKey) &&
        !string.IsNullOrWhiteSpace(RecaptchaProjectId) &&
        !string.IsNullOrWhiteSpace(RecaptchaApiKey) &&
        RecaptchaMinimumScore is >= 0 and <= 1 &&
        !string.IsNullOrWhiteSpace(ExpectedHostname) &&
        !string.IsNullOrWhiteSpace(RecipientEmail) &&
        !string.IsNullOrWhiteSpace(SmtpHost) &&
        SmtpPort is > 0 and <= 65535 &&
        !string.IsNullOrWhiteSpace(SmtpUsername) &&
        !string.IsNullOrWhiteSpace(SmtpPassword) &&
        !string.IsNullOrWhiteSpace(SmtpFromEmail);
}
