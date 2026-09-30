using JadByte.DurableMail.Retries;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace JadByte.DurableMail.Tests;

public sealed class RetryTests
{
    [Fact]
    public void DefaultsMatchThePublicContract()
    {
        var options = new DurableMailOptions();
        Assert.True(options.Dispatch.AutoDispatchOnEnqueue);
        Assert.Equal(25, options.Dispatch.BatchSize);
        Assert.Equal(1, options.Dispatch.MaxDegreeOfParallelism);
        Assert.Equal(TimeSpan.FromMinutes(2), options.Dispatch.LeaseDuration);
        Assert.Equal(5, options.Retries.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(2), options.Retries.InitialDelay);
        Assert.Equal(2, options.Retries.BackoffFactor);
        Assert.Equal(TimeSpan.FromMinutes(30), options.Retries.MaxDelay);
        Assert.Equal(0.15, options.Retries.JitterRatio);
        Assert.True(options.Retries.Scheduled.Enabled);
        Assert.False(options.Retries.Enqueue.Enabled);
        Assert.False(options.Retries.Startup.Enabled);
        Assert.False(options.Retries.Middleware.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Retries.Middleware.Interval);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(8, 30)]
    [InlineData(int.MaxValue, 30)]
    public void BackoffIsExponentialAndCapped(int attempt, int minutes)
    {
        var options = new EmailRetryOptions { JitterRatio = 0 };
        Assert.Equal(TimeSpan.FromMinutes(minutes), new ExponentialRetryDelayCalculator().Calculate(attempt, options));
    }

    [Fact]
    public void JitterStaysWithinConfiguredRange()
    {
        var calculator = new ExponentialRetryDelayCalculator();
        var options = new EmailRetryOptions();
        for (var index = 0; index < 500; index++)
        {
            Assert.InRange(calculator.Calculate(1, options).TotalMinutes, 1.7, 2.3);
        }
    }

    [Fact]
    public void TimerIsOneShotAndEarlierDeadlineWins()
    {
        var time = new FakeTimeProvider();
        using var schedule = new RetryScheduleCoordinator(time, Options.Create(new DurableMailOptions()), NullLogger<RetryScheduleCoordinator>.Instance);
        var signals = 0;
        schedule.Consider(time.GetUtcNow() + TimeSpan.FromMinutes(4), () => signals++);
        schedule.Consider(time.GetUtcNow() + TimeSpan.FromMinutes(2), () => signals++);
        schedule.Consider(time.GetUtcNow() + TimeSpan.FromMinutes(10), () => signals += 100);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, signals);
        time.Advance(TimeSpan.FromDays(7));
        Assert.Equal(1, signals);
    }

    [Fact]
    public void DueInPastFiresImmediatelyWithoutRepeating()
    {
        var time = new FakeTimeProvider();
        using var schedule = new RetryScheduleCoordinator(time, Options.Create(new DurableMailOptions()), NullLogger<RetryScheduleCoordinator>.Instance);
        var signals = 0;
        schedule.Consider(time.GetUtcNow() - TimeSpan.FromSeconds(1), () => signals++);
        time.Advance(TimeSpan.Zero);
        Assert.Equal(1, signals);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, signals);
    }

    [Fact]
    public void ReconciliationCancelsObsoleteLeaseTimer()
    {
        var time = new FakeTimeProvider();
        using var schedule = new RetryScheduleCoordinator(time, Options.Create(new DurableMailOptions()), NullLogger<RetryScheduleCoordinator>.Instance);
        var signals = 0;
        schedule.Consider(time.GetUtcNow() + TimeSpan.FromMinutes(2), () => signals++);
        schedule.Reconcile(null, () => signals++, schedule.GetGeneration());
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, signals);
    }

    [Fact]
    public void ReconciliationDoesNotOverwriteNewerSchedule()
    {
        var time = new FakeTimeProvider();
        using var schedule = new RetryScheduleCoordinator(time, Options.Create(new DurableMailOptions()), NullLogger<RetryScheduleCoordinator>.Instance);
        var signals = 0;
        var oldGeneration = schedule.GetGeneration();
        schedule.Consider(time.GetUtcNow() + TimeSpan.FromMinutes(1), () => signals++);
        schedule.Reconcile(null, () => signals++, oldGeneration);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, signals);
    }

    [Fact]
    public void DisabledSchedulerDoesNotArmTimer()
    {
        var time = new FakeTimeProvider();
        var options = new DurableMailOptions();
        options.Retries.Scheduled.Enabled = false;
        using var schedule = new RetryScheduleCoordinator(time, Options.Create(options), NullLogger<RetryScheduleCoordinator>.Instance);
        var signals = 0;
        schedule.Consider(time.GetUtcNow(), () => signals++);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, signals);
    }

    [Fact]
    public void InvalidOptionsFailValidationIncludingNonfiniteNumbers()
    {
        var validator = new DurableMailOptionsValidator();
        var options = new DurableMailOptions();
        options.Dispatch.BatchSize = 0;
        options.Retries.BackoffFactor = double.NaN;
        options.Retries.JitterRatio = double.PositiveInfinity;
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.True(result.Failures.Count() >= 3);
    }
}
