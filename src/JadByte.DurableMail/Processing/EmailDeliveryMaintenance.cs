using System.Transactions;
using JadByte.DurableMail.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace JadByte.DurableMail.Processing;

internal sealed class EmailDeliveryMaintenance(IServiceScopeFactory scopeFactory) : IEmailDeliveryMaintenance
{
    public async Task<EmailMaintenanceResult> PruneAsync(EmailPruneRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.BatchSize, 1);
        using var transaction = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailDeliveryStore>().PruneAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
