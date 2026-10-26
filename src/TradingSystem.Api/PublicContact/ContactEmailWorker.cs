using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Api.PublicContact;

public sealed class ContactEmailWorker(
    IServiceScopeFactory scopes,
    PublicContactOptions options,
    ILogger<ContactEmailWorker> logger,
    TimeProvider clock) : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> LogCycleFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(LogCycleFailed)),
            "Contact delivery cycle failed ({ErrorType})");
    private static readonly Action<ILogger, Guid, string, Exception?> LogDeliveryFailed =
        LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(2, nameof(LogDeliveryFailed)),
            "Contact email delivery failed for inquiry {InquiryId} ({ErrorType}); retry scheduled");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.IsReady)
            {
                try { await DeliverNextAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { LogCycleFailed(logger, error.GetType().Name, null); }
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    private async Task DeliverNextAsync(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var now = clock.GetUtcNow();
        var candidate = await db.ContactInquiries.AsNoTracking()
            .Where(item => item.NextDeliveryAtUtc <= now &&
                (item.DeliveryStatus == "Pending" || item.DeliveryStatus == "Sending"))
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new { item.Id })
            .FirstOrDefaultAsync(token);
        if (candidate is null) return;

        var claimed = await db.ContactInquiries
            .Where(item => item.Id == candidate.Id && item.NextDeliveryAtUtc <= now &&
                (item.DeliveryStatus == "Pending" || item.DeliveryStatus == "Sending"))
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.DeliveryStatus, "Sending")
                .SetProperty(item => item.DeliveryAttempts, item => item.DeliveryAttempts + 1)
                .SetProperty(item => item.NextDeliveryAtUtc, now.AddMinutes(5)), token);
        if (claimed != 1) return;
        var inquiry = await db.ContactInquiries.SingleAsync(item => item.Id == candidate.Id, token);
        try
        {
            await SendAsync(inquiry, token);
            inquiry.MarkDelivered(clock.GetUtcNow());
            await db.SaveChangesAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var delay = TimeSpan.FromMinutes(Math.Min(60, 1 << Math.Min(inquiry.DeliveryAttempts, 6)));
            inquiry.ScheduleRetry(clock.GetUtcNow().Add(delay));
            await db.SaveChangesAsync(token);
            LogDeliveryFailed(logger, inquiry.Id, error.GetType().Name, null);
        }
    }

    private async Task SendAsync(TradingSystem.Domain.Trading.ContactInquiry inquiry, CancellationToken token)
    {
        var email = new MimeMessage();
        email.From.Add(MailboxAddress.Parse(options.SmtpFromEmail));
        email.To.Add(MailboxAddress.Parse(options.RecipientEmail));
        email.ReplyTo.Add(new MailboxAddress(inquiry.Name, inquiry.Email));
        email.Subject = $"Sarthico enquiry: {inquiry.Service} [{inquiry.Id}]";
        email.Body = new TextPart("plain")
        {
            Text = $"Name: {inquiry.Name}\nEmail: {inquiry.Email}\nCompany: {inquiry.Company ?? "—"}\nService: {inquiry.Service}\n\n{inquiry.Message}"
        };
        using var smtp = new SmtpClient();
        smtp.Timeout = 15000;
        await smtp.ConnectAsync(options.SmtpHost, options.SmtpPort,
            options.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, token);
        await smtp.AuthenticateAsync(options.SmtpUsername, options.SmtpPassword, token);
        await smtp.SendAsync(email, token);
        await smtp.DisconnectAsync(true, token);
    }
}
