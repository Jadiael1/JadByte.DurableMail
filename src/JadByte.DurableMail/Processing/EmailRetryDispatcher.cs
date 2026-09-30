namespace JadByte.DurableMail.Processing;

internal sealed class EmailRetryDispatcher(EmailDispatchCoordinator coordinator) : IEmailRetryDispatcher
{
    public Task<EmailDispatchResult> DispatchDueAsync(CancellationToken cancellationToken = default) =>
        coordinator.DispatchDueAsync(cancellationToken);
}
