using JadByte.DurableMail.EntityFrameworkCore.Persistence.Entities;
using JadByte.DurableMail.Storage;
using Microsoft.EntityFrameworkCore;

namespace JadByte.DurableMail.EntityFrameworkCore;

internal sealed class EfEmailDeliveryStore<TContext>(TContext context) : IEmailDeliveryStore
    where TContext : DbContext
{
    private const int ConflictLimit = 8;
    private const int RecoveryBatchSize = 100;

    private DbSet<EmailDeliveryEntity> Deliveries => context.Set<EmailDeliveryEntity>();
    private DbSet<EmailDeliveryAttemptEntity> Attempts => context.Set<EmailDeliveryAttemptEntity>();
    private DbSet<EmailDeliverySlotEntity> Slots => context.Set<EmailDeliverySlotEntity>();

    public async Task<EmailEnqueueResult> EnqueueAsync(EmailEnqueueStoreRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        BeginOperation();
        try
        {
            for (var conflict = 0; conflict < ConflictLimit; conflict++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = request.Request.DeduplicationKey;
                var slot = key is null ? null : await Slots.AsTracking().SingleOrDefaultAsync(x => x.Key == key, cancellationToken).ConfigureAwait(false);
                var outcome = EmailEnqueueOutcome.Created;
                var delivery = CreateDelivery(request);
                if (slot is not null)
                {
                    var policy = request.Request.Policy ?? new EmailEnqueuePolicy();
                    var behavior = string.Equals(slot.Source, request.Request.Source, StringComparison.Ordinal)
                        ? policy.SameSource
                        : policy.DifferentSource;
                    if (behavior == ActiveDeliveryConflictBehavior.KeepExisting)
                    {
                        return new EmailEnqueueResult(slot.DeliveryId, EmailEnqueueOutcome.Existing);
                    }

                    var previous = await Deliveries.AsTracking().SingleAsync(x => x.Id == slot.DeliveryId, cancellationToken).ConfigureAwait(false);
                    previous.SupersededAt = request.Now;
                    previous.SupersededByDeliveryId = delivery.Id;
                    previous.UpdatedAt = request.Now;
                    previous.NextAttemptAt = null;
                    previous.Version = Guid.NewGuid();
                    if (previous.Status == EmailDeliveryStatus.Pending)
                    {
                        previous.Status = EmailDeliveryStatus.Superseded;
                    }

                    slot.DeliveryId = delivery.Id;
                    slot.Source = delivery.Source;
                    slot.Version = Guid.NewGuid();
                    outcome = EmailEnqueueOutcome.SupersededAndCreated;
                }
                else if (key is not null)
                {
                    Slots.Add(new EmailDeliverySlotEntity
                    {
                        Key = key,
                        DeliveryId = delivery.Id,
                        Source = delivery.Source,
                        Version = Guid.NewGuid(),
                    });
                }

                Deliveries.Add(delivery);
                try
                {
                    // A single SaveChanges transaction commits the delivery, supersession and slot together.
                    await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return new EmailEnqueueResult(delivery.Id, outcome);
                }
                catch (DbUpdateConcurrencyException)
                {
                    DetachMailEntities();
                }
                catch (DbUpdateException) when (key is not null && slot is null)
                {
                    // SaveChanges has rolled back. A fresh query distinguishes an INSERT race from a real failure.
                    DetachMailEntities();
                    if (!await Slots.AsNoTracking().AnyAsync(x => x.Key == key, cancellationToken).ConfigureAwait(false))
                    {
                        throw;
                    }
                }
            }

            throw new DurableMailException("The enqueue could not complete because its delivery slot remained under contention.");
        }
        finally
        {
            DetachMailEntities();
        }
    }

    public async Task<IReadOnlyList<Guid>> GetEligibleDeliveryIdsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        return await Deliveries.AsNoTracking()
            .Where(x => x.Status == EmailDeliveryStatus.Pending && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.NextAttemptAt ?? x.CreatedAt).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => x.Id).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClaimedEmailDelivery?> TryClaimAsync(Guid deliveryId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        BeginOperation();
        try
        {
            var delivery = await Deliveries.AsTracking().SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken).ConfigureAwait(false);
            if (delivery is null || delivery.Status != EmailDeliveryStatus.Pending || delivery.NextAttemptAt > now || delivery.AttemptCount >= delivery.MaxAttempts)
            {
                return null;
            }

            var previousVersion = delivery.Version;
            delivery.Status = EmailDeliveryStatus.Processing;
            delivery.LeaseId = Guid.NewGuid();
            delivery.LeaseExpiresAt = AddDelay(now, leaseDuration);
            delivery.AttemptCount++;
            delivery.LastAttemptAt = now;
            delivery.NextAttemptAt = null;
            delivery.UpdatedAt = now;
            delivery.Version = Guid.NewGuid();
            var attempt = new EmailDeliveryAttemptEntity
            {
                Id = Guid.NewGuid(),
                EmailDeliveryId = delivery.Id,
                AttemptNumber = delivery.AttemptCount,
                StartedAt = now,
            };
            Attempts.Add(attempt);
            try
            {
                await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                return null;
            }
            catch (DbUpdateException)
            {
                // Providers may insert the attempt before updating its parent. The unique attempt index
                // can therefore detect a lost claim before the delivery's concurrency token does.
                DetachMailEntities();
                var current = await Deliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken).ConfigureAwait(false);
                if (current is null || current.Version != previousVersion)
                {
                    return null;
                }

                throw;
            }

            return new ClaimedEmailDelivery(delivery.Id, attempt.Id, delivery.LeaseId.Value, delivery.Version,
                delivery.AttemptCount, delivery.MaxAttempts, new EmailMessage(delivery.Recipient, delivery.Subject, delivery.Body, delivery.BodyFormat));
        }
        finally
        {
            DetachMailEntities();
        }
    }

    public Task<bool> MarkSucceededAsync(ClaimedEmailDelivery claim, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        CompleteAsync(claim, now, EmailAttemptOutcome.Succeeded, null, null, cancellationToken);

    public Task<bool> MarkTransientFailureAsync(ClaimedEmailDelivery claim, DateTimeOffset now, DateTimeOffset nextAttemptAt,
        EmailFailureDetails error, CancellationToken cancellationToken = default) =>
        CompleteAsync(claim, now, EmailAttemptOutcome.TransientFailure, nextAttemptAt, error, cancellationToken);

    public Task<bool> MarkPermanentFailureAsync(ClaimedEmailDelivery claim, DateTimeOffset now,
        EmailFailureDetails error, CancellationToken cancellationToken = default) =>
        CompleteAsync(claim, now, EmailAttemptOutcome.PermanentFailure, null, error, cancellationToken);

    private async Task<bool> CompleteAsync(ClaimedEmailDelivery claim, DateTimeOffset now, EmailAttemptOutcome outcome,
        DateTimeOffset? nextAttemptAt, EmailFailureDetails? error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        BeginOperation();
        try
        {
            for (var conflict = 0; conflict < ConflictLimit; conflict++)
            {
                var delivery = await Deliveries.AsTracking().SingleOrDefaultAsync(x => x.Id == claim.DeliveryId, cancellationToken).ConfigureAwait(false);
                if (delivery is null || delivery.Status != EmailDeliveryStatus.Processing || delivery.LeaseId != claim.LeaseId ||
                    delivery.LeaseExpiresAt <= now || delivery.AttemptCount != claim.AttemptNumber ||
                    (delivery.Version != claim.Version && delivery.SupersededByDeliveryId is null))
                {
                    return false;
                }

                var attempt = await Attempts.AsTracking().SingleOrDefaultAsync(x => x.Id == claim.AttemptId && x.EmailDeliveryId == delivery.Id,
                    cancellationToken).ConfigureAwait(false);
                if (attempt is null || attempt.FinishedAt is not null || attempt.AttemptNumber != claim.AttemptNumber)
                {
                    return false;
                }

                attempt.FinishedAt = now;
                attempt.Outcome = outcome;
                attempt.ErrorType = Truncate(error?.ErrorType, 512);
                attempt.ErrorMessage = Truncate(error?.ErrorMessage, 4096);
                attempt.StackTrace = Truncate(error?.StackTrace, 16384);
                ClearLease(delivery, now);
                if (outcome == EmailAttemptOutcome.Succeeded)
                {
                    delivery.Status = EmailDeliveryStatus.Succeeded;
                    delivery.CompletedAt = now;
                }
                else if (delivery.SupersededByDeliveryId is not null)
                {
                    delivery.Status = EmailDeliveryStatus.Superseded;
                }
                else if (outcome == EmailAttemptOutcome.TransientFailure && delivery.AttemptCount < delivery.MaxAttempts)
                {
                    delivery.Status = EmailDeliveryStatus.Pending;
                    delivery.NextAttemptAt = nextAttemptAt;
                }
                else
                {
                    delivery.Status = EmailDeliveryStatus.Failed;
                    delivery.FailedAt = now;
                }

                if (delivery.Status != EmailDeliveryStatus.Pending)
                {
                    await ReleaseOwnedSlotAsync(delivery, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // A concurrent supersession may have changed Version without revoking this lease.
                    // Reload it; recovery or a newer claim always changes LeaseId and rejects completion.
                    DetachMailEntities();
                }
            }

            return false;
        }
        finally
        {
            DetachMailEntities();
        }
    }

    public async Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, Func<int, TimeSpan> retryDelay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retryDelay);
        BeginOperation();
        try
        {
            var recovered = 0;
            while (true)
            {
                var ids = await Deliveries.AsNoTracking()
                    .Where(x => x.Status == EmailDeliveryStatus.Processing && x.LeaseExpiresAt <= now)
                    .OrderBy(x => x.LeaseExpiresAt).ThenBy(x => x.Id).Select(x => x.Id)
                    .Take(RecoveryBatchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
                var progress = 0;
                foreach (var id in ids)
                {
                    if (await RecoverLeaseAsync(id, now, retryDelay, cancellationToken).ConfigureAwait(false))
                    {
                        progress++;
                    }
                }

                recovered += progress;
                if (ids.Count < RecoveryBatchSize || progress == 0)
                {
                    return recovered;
                }
            }
        }
        finally
        {
            DetachMailEntities();
        }
    }

    private async Task<bool> RecoverLeaseAsync(Guid deliveryId, DateTimeOffset now, Func<int, TimeSpan> retryDelay, CancellationToken cancellationToken)
    {
        try
        {
            var delivery = await Deliveries.AsTracking().SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken).ConfigureAwait(false);
            if (delivery is null || delivery.Status != EmailDeliveryStatus.Processing || delivery.LeaseExpiresAt > now)
            {
                return false;
            }

            var attempt = await Attempts.AsTracking().SingleOrDefaultAsync(x => x.EmailDeliveryId == deliveryId && x.AttemptNumber == delivery.AttemptCount,
                cancellationToken).ConfigureAwait(false);
            if (attempt is not null && attempt.FinishedAt is null)
            {
                attempt.FinishedAt = now;
                attempt.Outcome = EmailAttemptOutcome.Abandoned;
            }

            ClearLease(delivery, now);
            if (delivery.SupersededByDeliveryId is not null)
            {
                delivery.Status = EmailDeliveryStatus.Superseded;
            }
            else if (delivery.AttemptCount >= delivery.MaxAttempts)
            {
                delivery.Status = EmailDeliveryStatus.Failed;
                delivery.FailedAt = now;
            }
            else
            {
                delivery.Status = EmailDeliveryStatus.Pending;
                delivery.NextAttemptAt = AddDelay(now, retryDelay(delivery.AttemptCount));
            }

            if (delivery.Status != EmailDeliveryStatus.Pending)
            {
                await ReleaseOwnedSlotAsync(delivery, cancellationToken).ConfigureAwait(false);
            }

            await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        finally
        {
            DetachMailEntities();
        }
    }

    public async Task<DateTimeOffset?> GetNextWakeUpAtAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var pending = await Deliveries.AsNoTracking().Where(x => x.Status == EmailDeliveryStatus.Pending)
            .OrderBy(x => x.NextAttemptAt ?? x.CreatedAt).Select(x => (DateTimeOffset?)(x.NextAttemptAt ?? x.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var lease = await Deliveries.AsNoTracking().Where(x => x.Status == EmailDeliveryStatus.Processing && x.LeaseExpiresAt != null)
            .OrderBy(x => x.LeaseExpiresAt).Select(x => x.LeaseExpiresAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var next = pending is null ? lease : lease is null || pending <= lease ? pending : lease;
        return next < now ? now : next;
    }

    public async Task<EmailMaintenanceResult> PruneAsync(EmailPruneRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.BatchSize);
        BeginOperation();
        try
        {
            for (var conflict = 0; conflict < ConflictLimit; conflict++)
            {
                var deliveries = await Deliveries.AsTracking().Where(x =>
                        ((x.Status == EmailDeliveryStatus.Succeeded && request.SucceededBefore != null && x.CompletedAt < request.SucceededBefore) ||
                         (x.Status == EmailDeliveryStatus.Failed && request.FailedBefore != null && x.FailedAt < request.FailedBefore) ||
                         (x.Status == EmailDeliveryStatus.Superseded && request.SupersededBefore != null && x.SupersededAt < request.SupersededBefore)) &&
                        !Slots.Any(slot => slot.DeliveryId == x.Id))
                    .OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id).Take(request.BatchSize)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var ids = deliveries.Select(x => x.Id).ToArray();
                var attempts = await Attempts.CountAsync(x => ids.Contains(x.EmailDeliveryId), cancellationToken).ConfigureAwait(false);
                Deliveries.RemoveRange(deliveries);
                try
                {
                    await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return new EmailMaintenanceResult(deliveries.Count, attempts);
                }
                catch (DbUpdateConcurrencyException)
                {
                    DetachMailEntities();
                }
            }

            return new EmailMaintenanceResult(0, 0);
        }
        finally
        {
            DetachMailEntities();
        }
    }

    private async Task ReleaseOwnedSlotAsync(EmailDeliveryEntity delivery, CancellationToken cancellationToken)
    {
        if (delivery.DeduplicationKey is not null)
        {
            var slot = await Slots.AsTracking().SingleOrDefaultAsync(x => x.Key == delivery.DeduplicationKey && x.DeliveryId == delivery.Id,
                cancellationToken).ConfigureAwait(false);
            if (slot is not null)
            {
                Slots.Remove(slot);
            }
        }
    }

    private static EmailDeliveryEntity CreateDelivery(EmailEnqueueStoreRequest request) => new()
    {
        Id = Guid.NewGuid(),
        Recipient = request.Request.Message.Recipient,
        Subject = request.Request.Message.Subject,
        Body = request.Request.Message.Body,
        BodyFormat = request.Request.Message.BodyFormat,
        Source = request.Request.Source,
        DeduplicationKey = request.Request.DeduplicationKey,
        Status = EmailDeliveryStatus.Pending,
        MaxAttempts = request.MaxAttempts,
        CreatedAt = request.Now,
        UpdatedAt = request.Now,
        Version = Guid.NewGuid(),
    };

    private static void ClearLease(EmailDeliveryEntity delivery, DateTimeOffset now)
    {
        delivery.LeaseId = null;
        delivery.LeaseExpiresAt = null;
        delivery.NextAttemptAt = null;
        delivery.UpdatedAt = now;
        delivery.Version = Guid.NewGuid();
    }

    private static DateTimeOffset AddDelay(DateTimeOffset now, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return now.ToUniversalTime();
        }

        return delay.Ticks > DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(now.UtcTicks + delay.Ticks, TimeSpan.Zero);
    }

    private void BeginOperation()
    {
        if (context.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never || context.Database.CurrentTransaction is not null ||
            System.Transactions.Transaction.Current is not null)
        {
            throw new DurableMailConfigurationException("DurableMail requires automatic EF Core transactions and an independent context without an existing transaction.");
        }

        context.ChangeTracker.DetectChanges();
        if (context.ChangeTracker.Entries().Any(entry => !IsMailEntity(entry.Entity) &&
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            throw new DurableMailConfigurationException("DurableMail requires an independent context without pending business changes.");
        }

        DetachMailEntities();
    }

    private Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        // Honor contexts with AutoDetectChanges disabled without changing the consumer's setting.
        context.ChangeTracker.DetectChanges();
        return context.SaveChangesAsync(cancellationToken);
    }

    private void DetachMailEntities()
    {
        foreach (var entry in context.ChangeTracker.Entries().Where(entry => IsMailEntity(entry.Entity)).ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool IsMailEntity(object entity) =>
        entity is EmailDeliveryEntity or EmailDeliveryAttemptEntity or EmailDeliverySlotEntity;

    private static string? Truncate(string? value, int length)
    {
        if (value is null || value.Length <= length)
        {
            return value;
        }

        // Do not persist a dangling high surrogate when truncation splits a UTF-16 pair.
        return value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];
    }
}
