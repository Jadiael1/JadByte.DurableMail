namespace JadByte.DurableMail.EntityFrameworkCore.Persistence.Entities;

internal enum EmailDeliveryStatus
{
    Pending = 0,
    Processing = 1,
    Succeeded = 2,
    Failed = 3,
    Superseded = 4,
}

internal sealed class EmailDeliveryEntity
{
    public Guid Id { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public EmailBodyFormat BodyFormat { get; set; }
    public EmailDeliveryStatus Status { get; set; }
    public string? Source { get; set; }
    public string? DeduplicationKey { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public Guid? SupersededByDeliveryId { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public Guid Version { get; set; }
}
