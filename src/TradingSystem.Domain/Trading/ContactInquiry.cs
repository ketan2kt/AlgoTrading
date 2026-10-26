using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

public sealed class ContactInquiry : MutableEntity
{
    private ContactInquiry() : base(Guid.NewGuid(), DateTimeOffset.UtcNow) { }

    public ContactInquiry(string name, string email, string? company, string service, string message, DateTimeOffset now)
        : base(Guid.NewGuid(), now)
    {
        Name = name;
        Email = email;
        Company = company;
        Service = service;
        Message = message;
        NextDeliveryAtUtc = now.ToUniversalTime();
    }

    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string? Company { get; private set; }
    public string Service { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
    public string DeliveryStatus { get; private set; } = "Pending";
    public int DeliveryAttempts { get; private set; }
    public DateTimeOffset NextDeliveryAtUtc { get; private set; }
    public DateTimeOffset? DeliveredAtUtc { get; private set; }

    public void MarkSending(DateTimeOffset leaseUntilUtc)
    {
        DeliveryStatus = "Sending";
        DeliveryAttempts++;
        NextDeliveryAtUtc = leaseUntilUtc.ToUniversalTime();
    }

    public void MarkDelivered(DateTimeOffset now)
    {
        DeliveryStatus = "Delivered";
        DeliveredAtUtc = now.ToUniversalTime();
    }

    public void ScheduleRetry(DateTimeOffset when)
    {
        DeliveryStatus = "Pending";
        NextDeliveryAtUtc = when.ToUniversalTime();
    }
}
