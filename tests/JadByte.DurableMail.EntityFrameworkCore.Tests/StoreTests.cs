using JadByte.DurableMail.EntityFrameworkCore.Persistence.Entities;
using JadByte.DurableMail.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JadByte.DurableMail.EntityFrameworkCore.Tests;

public sealed partial class StoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly EmailFailureDetails Failure = new("TemporaryFailure", "Safe diagnostic", null);

    [Fact]
    public async Task ModelCreatesThreeExplicitTablesAndRequiredIndexes()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var mailTypes = context.Model.GetEntityTypes().Where(x => x.ClrType != typeof(BusinessEntity)).ToArray();
        Assert.Equal(["durable_mail_attempts", "durable_mail_deliveries", "durable_mail_slots"],
            mailTypes.Select(x => x.GetTableName()).Order().ToArray());
        Assert.All(mailTypes.SelectMany(x => x.GetProperties()), x => Assert.DoesNotContain(x.GetColumnName(), char.IsUpper));
        var delivery = context.Model.FindEntityType(typeof(EmailDeliveryEntity))!;
        Assert.True(delivery.FindProperty(nameof(EmailDeliveryEntity.Version))!.IsConcurrencyToken);
        Assert.Equal(typeof(long), delivery.FindProperty(nameof(EmailDeliveryEntity.CreatedAt))!.GetValueConverter()!.ProviderClrType);
        Assert.Equal(3, delivery.GetIndexes().Count());
        Assert.True(context.Model.FindEntityType(typeof(EmailDeliveryAttemptEntity))!.GetIndexes().Single().IsUnique);
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE 'durable_mail_%'";
        Assert.Equal(3L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoKeyAlwaysCreatesIndependentDeliveriesAndTimestampsNormalizeToUtc()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(context);
        var request = Request() with { Now = Now.ToOffset(TimeSpan.FromHours(-3)) };
        var first = await store.EnqueueAsync(request, TestContext.Current.CancellationToken);
        var second = await store.EnqueueAsync(request, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.DeliveryId, second.DeliveryId);
        Assert.Equal(EmailEnqueueOutcome.Created, first.Outcome);
        Assert.Empty(await context.Set<EmailDeliverySlotEntity>().ToListAsync(TestContext.Current.CancellationToken));
        Assert.All(await context.Set<EmailDeliveryEntity>().ToListAsync(TestContext.Current.CancellationToken), x =>
        {
            Assert.Equal(Now, x.CreatedAt);
            Assert.Equal(TimeSpan.Zero, x.CreatedAt.Offset);
        });
    }

    [Theory]
    [InlineData("source")]
    [InlineData("another-source")]
    [InlineData(null)]
    public async Task DefaultPolicyReusesExistingDelivery(string? source)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(context);
        var first = await store.EnqueueAsync(Request("key", "source"), TestContext.Current.CancellationToken);
        var second = await store.EnqueueAsync(Request("key", source), TestContext.Current.CancellationToken);
        Assert.Equal(first.DeliveryId, second.DeliveryId);
        Assert.Equal(EmailEnqueueOutcome.Existing, second.Outcome);
        Assert.Equal(1, await context.Set<EmailDeliveryEntity>().CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SupersedingPendingTransfersSlotAndPreventsClaimingOldDelivery()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(context);
        var first = await store.EnqueueAsync(Request("key", "initial"), TestContext.Current.CancellationToken);
        var replacement = await store.EnqueueAsync(Request("key", "replacement", Supersede()), TestContext.Current.CancellationToken);
        Assert.Equal(EmailEnqueueOutcome.SupersededAndCreated, replacement.Outcome);
        Assert.Null(await store.TryClaimAsync(first.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        var previous = await context.Set<EmailDeliveryEntity>().SingleAsync(x => x.Id == first.DeliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(EmailDeliveryStatus.Superseded, previous.Status);
        Assert.Equal(replacement.DeliveryId, previous.SupersededByDeliveryId);
        Assert.Equal(replacement.DeliveryId, (await context.Set<EmailDeliverySlotEntity>().SingleAsync(TestContext.Current.CancellationToken)).DeliveryId);
        Assert.Equal([replacement.DeliveryId], await store.GetEligibleDeliveryIdsAsync(Now, 10, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessingSupersessionPreservesRealSuccessOrSuppressesRetry(bool succeeds)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(context);
        var first = await store.EnqueueAsync(Request("key", "initial"), TestContext.Current.CancellationToken);
        var claim = Assert.IsType<ClaimedEmailDelivery>(await store.TryClaimAsync(first.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        var replacement = await store.EnqueueAsync(Request("key", "replacement", Supersede()), TestContext.Current.CancellationToken);
        var completed = succeeds
            ? await store.MarkSucceededAsync(claim, Now.AddSeconds(1), TestContext.Current.CancellationToken)
            : await store.MarkTransientFailureAsync(claim, Now.AddSeconds(1), Now.AddMinutes(2), Failure, TestContext.Current.CancellationToken);
        Assert.True(completed);
        var previous = await context.Set<EmailDeliveryEntity>().SingleAsync(x => x.Id == first.DeliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(succeeds ? EmailDeliveryStatus.Succeeded : EmailDeliveryStatus.Superseded, previous.Status);
        Assert.Null(previous.LeaseId);
        Assert.Null(previous.NextAttemptAt);
        Assert.Equal(replacement.DeliveryId, (await context.Set<EmailDeliverySlotEntity>().SingleAsync(TestContext.Current.CancellationToken)).DeliveryId);
        var attempt = await context.Set<EmailDeliveryAttemptEntity>().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(succeeds ? EmailAttemptOutcome.Succeeded : EmailAttemptOutcome.TransientFailure, attempt.Outcome);
    }

    [Fact]
    public async Task TransientFailureSchedulesRetryAndExhaustionReleasesSlot()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(context);
        var enqueued = await store.EnqueueAsync(Request("key") with { MaxAttempts = 2 }, TestContext.Current.CancellationToken);
        var first = Assert.IsType<ClaimedEmailDelivery>(await store.TryClaimAsync(enqueued.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        var next = Now.AddMinutes(3);
        Assert.True(await store.MarkTransientFailureAsync(first, Now.AddSeconds(1), next, Failure, TestContext.Current.CancellationToken));
        Assert.Empty(await store.GetEligibleDeliveryIdsAsync(Now.AddMinutes(1), 10, TestContext.Current.CancellationToken));
        Assert.Equal(next, await store.GetNextWakeUpAtAsync(Now, TestContext.Current.CancellationToken));
        Assert.Null(await store.TryClaimAsync(enqueued.DeliveryId, next.AddTicks(-1), Lease, TestContext.Current.CancellationToken));
        var second = Assert.IsType<ClaimedEmailDelivery>(await store.TryClaimAsync(enqueued.DeliveryId, next, Lease, TestContext.Current.CancellationToken));
        Assert.Equal(2, second.AttemptNumber);
        Assert.True(await store.MarkTransientFailureAsync(second, next.AddSeconds(1), next.AddMinutes(4), Failure, TestContext.Current.CancellationToken));
        var delivery = await context.Set<EmailDeliveryEntity>().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EmailDeliveryStatus.Failed, delivery.Status);
        Assert.NotNull(delivery.FailedAt);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Empty(await context.Set<EmailDeliverySlotEntity>().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await context.Set<EmailDeliveryAttemptEntity>().CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PermanentFailureTerminatesImmediatelyAndTruncatesDiagnosticsSafely()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(context);
        var enqueued = await store.EnqueueAsync(Request("key"), TestContext.Current.CancellationToken);
        var claim = Assert.IsType<ClaimedEmailDelivery>(await store.TryClaimAsync(enqueued.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        var failure = new EmailFailureDetails(new string('t', 600), new string('m', 4095) + "😀", new string('s', 17000));
        Assert.True(await store.MarkPermanentFailureAsync(claim, Now.AddSeconds(1), failure, TestContext.Current.CancellationToken));
        Assert.Null(await store.GetNextWakeUpAtAsync(Now, TestContext.Current.CancellationToken));
        Assert.Empty(await context.Set<EmailDeliverySlotEntity>().ToListAsync(TestContext.Current.CancellationToken));
        var attempt = await context.Set<EmailDeliveryAttemptEntity>().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(512, attempt.ErrorType!.Length);
        Assert.Equal(4095, attempt.ErrorMessage!.Length);
        Assert.Equal(16384, attempt.StackTrace!.Length);
        Assert.Equal(EmailAttemptOutcome.PermanentFailure, attempt.Outcome);
    }

    [Fact]
    public async Task FutureProcessingLeaseIsIncludedInNextWakeUpAndRecoveredAtExpiry()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(context);
        var enqueued = await store.EnqueueAsync(Request(), TestContext.Current.CancellationToken);
        var claim = Assert.IsType<ClaimedEmailDelivery>(await store.TryClaimAsync(enqueued.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        Assert.Equal(Now + Lease, await store.GetNextWakeUpAtAsync(Now, TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.RecoverExpiredLeasesAsync(Now + Lease - TimeSpan.FromTicks(1), _ => TimeSpan.Zero, TestContext.Current.CancellationToken));
        Assert.False(await store.MarkSucceededAsync(claim, Now + Lease, TestContext.Current.CancellationToken));
        var recoveryTime = Now + Lease;
        Assert.Equal(1, await store.RecoverExpiredLeasesAsync(recoveryTime, count => TimeSpan.FromMinutes(count * 2), TestContext.Current.CancellationToken));
        var delivery = await context.Set<EmailDeliveryEntity>().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EmailDeliveryStatus.Pending, delivery.Status);
        Assert.Equal(recoveryTime.AddMinutes(2), delivery.NextAttemptAt);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(EmailAttemptOutcome.Abandoned, (await context.Set<EmailDeliveryAttemptEntity>().SingleAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task StaleWorkerCannotOverwriteRecoveredAndReclaimedDelivery()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var firstStore = new EfEmailDeliveryStore<MailContext>(firstContext);
        var secondStore = new EfEmailDeliveryStore<MailContext>(secondContext);
        var enqueued = await firstStore.EnqueueAsync(Request("key"), TestContext.Current.CancellationToken);
        var stale = Assert.IsType<ClaimedEmailDelivery>(await firstStore.TryClaimAsync(enqueued.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        var recoveredAt = Now + Lease;
        Assert.Equal(1, await secondStore.RecoverExpiredLeasesAsync(recoveredAt, _ => TimeSpan.Zero, TestContext.Current.CancellationToken));
        var current = Assert.IsType<ClaimedEmailDelivery>(await secondStore.TryClaimAsync(enqueued.DeliveryId, recoveredAt, Lease, TestContext.Current.CancellationToken));
        Assert.False(await firstStore.MarkSucceededAsync(stale, recoveredAt.AddSeconds(1), TestContext.Current.CancellationToken));
        Assert.False(await firstStore.MarkTransientFailureAsync(stale, recoveredAt.AddSeconds(1), recoveredAt.AddMinutes(4), Failure, TestContext.Current.CancellationToken));
        Assert.True(await secondStore.MarkSucceededAsync(current, recoveredAt.AddSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(EmailDeliveryStatus.Succeeded, (await firstContext.Set<EmailDeliveryEntity>().SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal([EmailAttemptOutcome.Abandoned, EmailAttemptOutcome.Succeeded], await firstContext.Set<EmailDeliveryAttemptEntity>()
            .OrderBy(x => x.AttemptNumber).Select(x => x.Outcome).ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryTerminatesExhaustedOrSupersededProcessing(bool superseded)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(context);
        var enqueued = await store.EnqueueAsync(Request("key", "initial") with { MaxAttempts = superseded ? 5 : 1 }, TestContext.Current.CancellationToken);
        var claim = Assert.IsType<ClaimedEmailDelivery>(await store.TryClaimAsync(enqueued.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        EmailEnqueueResult? replacement = null;
        if (superseded)
        {
            replacement = await store.EnqueueAsync(Request("key", "replacement", Supersede()), TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, await store.RecoverExpiredLeasesAsync(Now + Lease, _ => throw new InvalidOperationException("Terminal recovery must not calculate retry delay."), TestContext.Current.CancellationToken));
        Assert.False(await store.MarkSucceededAsync(claim, Now + Lease, TestContext.Current.CancellationToken));
        var previous = await context.Set<EmailDeliveryEntity>().SingleAsync(x => x.Id == enqueued.DeliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(superseded ? EmailDeliveryStatus.Superseded : EmailDeliveryStatus.Failed, previous.Status);
        Assert.Null(previous.NextAttemptAt);
        var slots = await context.Set<EmailDeliverySlotEntity>().ToListAsync(TestContext.Current.CancellationToken);
        if (superseded)
        {
            Assert.Equal(replacement!.DeliveryId, Assert.Single(slots).DeliveryId);
        }
        else
        {
            Assert.Empty(slots);
        }
    }

    [Fact]
    public async Task ConcurrentClaimAcrossContextsCreatesExactlyOneAttempt()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var enqueued = await new EfEmailDeliveryStore<MailContext>(setup).EnqueueAsync(Request(), TestContext.Current.CancellationToken);
        var barrier = new SaveBarrierInterceptor();
        await using var first = database.CreateContext(barrier);
        await using var second = database.CreateContext(barrier);
        var claims = await Task.WhenAll(
            new EfEmailDeliveryStore<MailContext>(first).TryClaimAsync(enqueued.DeliveryId, Now, Lease, TestContext.Current.CancellationToken),
            new EfEmailDeliveryStore<MailContext>(second).TryClaimAsync(enqueued.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        Assert.Single(claims, claim => claim is not null);
        Assert.Equal(1, await setup.Set<EmailDeliveryAttemptEntity>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, (await setup.Set<EmailDeliveryEntity>().SingleAsync(TestContext.Current.CancellationToken)).AttemptCount);
    }

    [Fact]
    public async Task ConcurrentSlotInsertRollsBackLosingDeliveryAndRequeriesWinner()
    {
        await using var database = await TestDatabase.CreateAsync();
        var barrier = new SaveBarrierInterceptor();
        await using var first = database.CreateContext(barrier);
        await using var second = database.CreateContext(barrier);
        var results = await Task.WhenAll(
            new EfEmailDeliveryStore<MailContext>(first).EnqueueAsync(Request("key"), TestContext.Current.CancellationToken),
            new EfEmailDeliveryStore<MailContext>(second).EnqueueAsync(Request("key"), TestContext.Current.CancellationToken));
        Assert.Equal(results[0].DeliveryId, results[1].DeliveryId);
        Assert.Single(results, result => result.Outcome == EmailEnqueueOutcome.Created);
        Assert.Single(results, result => result.Outcome == EmailEnqueueOutcome.Existing);
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Set<EmailDeliveryEntity>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.Set<EmailDeliverySlotEntity>().CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentSupersessionKeepsOneReplacementAndNoOrphan()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        await new EfEmailDeliveryStore<MailContext>(setup).EnqueueAsync(Request("key", "initial"), TestContext.Current.CancellationToken);
        var barrier = new SaveBarrierInterceptor();
        await using var first = database.CreateContext(barrier);
        await using var second = database.CreateContext(barrier);
        var results = await Task.WhenAll(
            new EfEmailDeliveryStore<MailContext>(first).EnqueueAsync(Request("key", "replacement", Supersede()), TestContext.Current.CancellationToken),
            new EfEmailDeliveryStore<MailContext>(second).EnqueueAsync(Request("key", "replacement", Supersede()), TestContext.Current.CancellationToken));
        Assert.Equal(results[0].DeliveryId, results[1].DeliveryId);
        Assert.Equal(2, await setup.Set<EmailDeliveryEntity>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await setup.Set<EmailDeliveryEntity>().CountAsync(x => x.Status == EmailDeliveryStatus.Pending, TestContext.Current.CancellationToken));
        Assert.Equal(1, await setup.Set<EmailDeliveryEntity>().CountAsync(x => x.Status == EmailDeliveryStatus.Superseded, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompletionRacingSupersessionCannotDeleteTheReplacementSlot()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var setupStore = new EfEmailDeliveryStore<MailContext>(setup);
        var original = await setupStore.EnqueueAsync(Request("key", "initial"), TestContext.Current.CancellationToken);
        var claim = Assert.IsType<ClaimedEmailDelivery>(await setupStore.TryClaimAsync(original.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        var barrier = new SaveBarrierInterceptor();
        await using var completionContext = database.CreateContext(barrier);
        await using var replacementContext = database.CreateContext(barrier);
        var completion = new EfEmailDeliveryStore<MailContext>(completionContext).MarkSucceededAsync(claim, Now.AddSeconds(1), TestContext.Current.CancellationToken);
        var replacement = new EfEmailDeliveryStore<MailContext>(replacementContext)
            .EnqueueAsync(Request("key", "replacement", Supersede()), TestContext.Current.CancellationToken);
        await Task.WhenAll(completion, replacement);
        Assert.True(await completion);
        var result = await replacement;
        Assert.Equal(result.DeliveryId, (await setup.Set<EmailDeliverySlotEntity>().SingleAsync(TestContext.Current.CancellationToken)).DeliveryId);
        Assert.Equal(EmailDeliveryStatus.Succeeded, (await setup.Set<EmailDeliveryEntity>()
            .SingleAsync(x => x.Id == original.DeliveryId, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(2, await setup.Set<EmailDeliveryEntity>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(EmailAttemptOutcome.Succeeded, (await setup.Set<EmailDeliveryAttemptEntity>().SingleAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task FailedInsertWithoutCompetingSlotRethrowsOriginalException()
    {
        await using var database = await TestDatabase.CreateAsync();
        var original = new DbUpdateException("A real persistence failure.");
        await using var context = database.CreateContext(new FailSaveInterceptor(original));
        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() =>
            new EfEmailDeliveryStore<MailContext>(context).EnqueueAsync(Request("key"), TestContext.Current.CancellationToken));
        Assert.Same(original, thrown);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GuidVersionRejectsConflictingWritesAcrossContexts()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        await new EfEmailDeliveryStore<MailContext>(first).EnqueueAsync(Request(), TestContext.Current.CancellationToken);
        var firstDelivery = await first.Set<EmailDeliveryEntity>().SingleAsync(TestContext.Current.CancellationToken);
        var secondDelivery = await second.Set<EmailDeliveryEntity>().SingleAsync(TestContext.Current.CancellationToken);
        firstDelivery.Version = Guid.NewGuid();
        secondDelivery.Version = Guid.NewGuid();
        await first.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DatabaseEnforcesUniqueSlotAndAttemptNumber()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(setup);
        var enqueued = await store.EnqueueAsync(Request("key"), TestContext.Current.CancellationToken);
        await store.TryClaimAsync(enqueued.DeliveryId, Now, Lease, TestContext.Current.CancellationToken);
        await using var slotContext = database.CreateContext();
        slotContext.Add(new EmailDeliverySlotEntity { Key = "key", DeliveryId = enqueued.DeliveryId, Version = Guid.NewGuid() });
        await Assert.ThrowsAsync<DbUpdateException>(() => slotContext.SaveChangesAsync(TestContext.Current.CancellationToken));
        await using var attemptContext = database.CreateContext();
        attemptContext.Add(new EmailDeliveryAttemptEntity { Id = Guid.NewGuid(), EmailDeliveryId = enqueued.DeliveryId, AttemptNumber = 1, StartedAt = Now });
        await Assert.ThrowsAsync<DbUpdateException>(() => attemptContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PruningDeletesOnlyTerminalDeliveriesAndCascadesAttemptsInBoundedBatches()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(context);
        var succeeded = await store.EnqueueAsync(Request("success"), TestContext.Current.CancellationToken);
        var successClaim = Assert.IsType<ClaimedEmailDelivery>(await store.TryClaimAsync(succeeded.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        await store.MarkSucceededAsync(successClaim, Now.AddSeconds(1), TestContext.Current.CancellationToken);
        var failed = await store.EnqueueAsync(Request("failure"), TestContext.Current.CancellationToken);
        var failureClaim = Assert.IsType<ClaimedEmailDelivery>(await store.TryClaimAsync(failed.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        await store.MarkPermanentFailureAsync(failureClaim, Now.AddSeconds(1), Failure, TestContext.Current.CancellationToken);
        await store.EnqueueAsync(Request("pending"), TestContext.Current.CancellationToken);
        var processing = await store.EnqueueAsync(Request("processing"), TestContext.Current.CancellationToken);
        await store.TryClaimAsync(processing.DeliveryId, Now, Lease, TestContext.Current.CancellationToken);
        var cutoff = Now.AddDays(1);
        var first = await store.PruneAsync(new(cutoff, cutoff, cutoff, BatchSize: 1), TestContext.Current.CancellationToken);
        Assert.Equal(new EmailMaintenanceResult(1, 1), first);
        var second = await store.PruneAsync(new(cutoff, cutoff, cutoff), TestContext.Current.CancellationToken);
        Assert.Equal(new EmailMaintenanceResult(1, 1), second);
        Assert.Equal(2, await context.Set<EmailDeliveryEntity>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await context.Set<EmailDeliverySlotEntity>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await context.Set<EmailDeliveryAttemptEntity>().CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StorePersistsTransitionsWhenConsumerDisablesTrackingDefaults()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        var store = new EfEmailDeliveryStore<MailContext>(context);
        var first = await store.EnqueueAsync(Request("key", "initial"), TestContext.Current.CancellationToken);
        var claim = Assert.IsType<ClaimedEmailDelivery>(await store.TryClaimAsync(first.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        var replacement = await store.EnqueueAsync(Request("key", "replacement", Supersede()), TestContext.Current.CancellationToken);
        Assert.True(await store.MarkSucceededAsync(claim, Now.AddSeconds(1), TestContext.Current.CancellationToken));
        var replacementClaim = Assert.IsType<ClaimedEmailDelivery>(await store.TryClaimAsync(replacement.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
        Assert.True(await store.MarkPermanentFailureAsync(replacementClaim, Now.AddSeconds(1), Failure, TestContext.Current.CancellationToken));
        Assert.Equal(EmailDeliveryStatus.Succeeded, (await context.Set<EmailDeliveryEntity>()
            .SingleAsync(x => x.Id == first.DeliveryId, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(EmailDeliveryStatus.Failed, (await context.Set<EmailDeliveryEntity>()
            .SingleAsync(x => x.Id == replacement.DeliveryId, TestContext.Current.CancellationToken)).Status);
        Assert.False(await context.Set<EmailDeliverySlotEntity>().AnyAsync(TestContext.Current.CancellationToken));
        Assert.False(context.ChangeTracker.AutoDetectChangesEnabled);
        Assert.Equal(QueryTrackingBehavior.NoTracking, context.ChangeTracker.QueryTrackingBehavior);
    }

    [Fact]
    public async Task MaximumLeaseAndRecoveryDelaySaturateAtTheLatestRepresentableTimestamp()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(context);
        var longLease = await store.EnqueueAsync(Request(), TestContext.Current.CancellationToken);
        Assert.NotNull(await store.TryClaimAsync(longLease.DeliveryId, Now, TimeSpan.MaxValue, TestContext.Current.CancellationToken));
        Assert.Equal(DateTimeOffset.MaxValue, await store.GetNextWakeUpAtAsync(Now, TestContext.Current.CancellationToken));
        var retry = await store.EnqueueAsync(Request(), TestContext.Current.CancellationToken);
        await store.TryClaimAsync(retry.DeliveryId, Now, Lease, TestContext.Current.CancellationToken);
        Assert.Equal(1, await store.RecoverExpiredLeasesAsync(Now + Lease, _ => TimeSpan.MaxValue, TestContext.Current.CancellationToken));
        Assert.Equal(DateTimeOffset.MaxValue, await store.GetNextWakeUpAtAsync(Now + Lease, TestContext.Current.CancellationToken));
        var delivery = await context.Set<EmailDeliveryEntity>().SingleAsync(x => x.Id == retry.DeliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(DateTimeOffset.MaxValue, delivery.NextAttemptAt);
        Assert.Equal(EmailDeliveryStatus.Pending, delivery.Status);
    }

    [Fact]
    public async Task ContextWithBusinessChangesOrDisabledTransactionsIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfEmailDeliveryStore<MailContext>(context);
        context.Add(new BusinessEntity { Id = Guid.NewGuid() });
        await Assert.ThrowsAsync<DurableMailConfigurationException>(() => store.EnqueueAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(EntityState.Added, Assert.Single(context.ChangeTracker.Entries()).State);
        context.ChangeTracker.Clear();
        context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        await Assert.ThrowsAsync<DurableMailConfigurationException>(() => store.EnqueueAsync(Request(), TestContext.Current.CancellationToken));
        context.Database.AutoTransactionBehavior = AutoTransactionBehavior.WhenNeeded;
        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DurableMailConfigurationException>(() => store.EnqueueAsync(Request(), TestContext.Current.CancellationToken));
    }

    private static EmailEnqueueStoreRequest Request(string? key = null, string? source = "source", EmailEnqueuePolicy? policy = null) =>
        new(new(new EmailMessage("recipient@example.test", "Subject", "Body"), source, key, policy), 5, Now);

    private static EmailEnqueuePolicy Supersede() => new() { DifferentSource = ActiveDeliveryConflictBehavior.SupersedeExisting };

    private sealed class MailContext(DbContextOptions<MailContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddDurableMail();
            modelBuilder.Entity<BusinessEntity>().HasKey(x => x.Id);
        }
    }

    private sealed class BusinessEntity
    {
        public Guid Id { get; set; }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"durable-mail-{Guid.NewGuid():N}.db");

        public static async Task<TestDatabase> CreateAsync()
        {
            var database = new TestDatabase();
            await using var context = database.CreateContext();
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return database;
        }

        public MailContext CreateContext(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<MailContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Default Timeout=10")
            .AddInterceptors(interceptors).Options);

        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SaveBarrierInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                bothArrived.TrySetResult();
            }

            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class FailSaveInterceptor(DbUpdateException exception) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw exception;
    }
}
