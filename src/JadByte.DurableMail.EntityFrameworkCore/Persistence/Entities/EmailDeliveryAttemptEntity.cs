namespace JadByte.DurableMail.EntityFrameworkCore.Persistence.Entities;

internal enum EmailAttemptOutcome
{
    Succeeded = 0,
    TransientFailure = 1,
    PermanentFailure = 2,
    Abandoned = 3,
}

internal sealed class EmailDeliveryAttemptEntity
{
    public Guid Id { get; set; }
    public Guid EmailDeliveryId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public EmailAttemptOutcome? Outcome { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
}
