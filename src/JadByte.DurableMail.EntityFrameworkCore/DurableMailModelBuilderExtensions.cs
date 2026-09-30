using JadByte.DurableMail.EntityFrameworkCore.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace JadByte.DurableMail.EntityFrameworkCore;

/// <summary>Configures the provider-independent DurableMail persistence model.</summary>
public static class DurableMailModelBuilderExtensions
{
    /// <summary>Adds the three DurableMail tables with explicit column and index names.</summary>
    /// <remarks>
    /// Timestamps are persisted as UTC ticks in 64-bit integer columns. This preserves ordering and
    /// comparisons on providers, including SQLite, that cannot compare native DateTimeOffset values.
    /// The original UTC offset is normalized. Generate migrations using the application's provider.
    /// </remarks>
    /// <param name="modelBuilder">The application's model builder.</param>
    /// <returns>The model builder for further configuration.</returns>
    public static ModelBuilder AddDurableMail(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var timestamp = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            value => new DateTimeOffset(value, TimeSpan.Zero));

        var delivery = modelBuilder.Entity<EmailDeliveryEntity>();
        delivery.ToTable("durable_mail_deliveries");
        delivery.HasKey(x => x.Id).HasName("pk_durable_mail_deliveries");
        delivery.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        delivery.Property(x => x.Recipient).HasColumnName("recipient").IsRequired();
        delivery.Property(x => x.Subject).HasColumnName("subject").IsRequired();
        delivery.Property(x => x.Body).HasColumnName("body").IsRequired();
        delivery.Property(x => x.BodyFormat).HasColumnName("body_format").HasConversion<int>();
        delivery.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
        delivery.Property(x => x.Source).HasColumnName("source").HasMaxLength(128);
        delivery.Property(x => x.DeduplicationKey).HasColumnName("deduplication_key").HasMaxLength(256);
        delivery.Property(x => x.AttemptCount).HasColumnName("attempt_count");
        delivery.Property(x => x.MaxAttempts).HasColumnName("max_attempts");
        delivery.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(timestamp);
        delivery.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasConversion(timestamp);
        delivery.Property(x => x.LastAttemptAt).HasColumnName("last_attempt_at").HasConversion(timestamp);
        delivery.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasConversion(timestamp);
        delivery.Property(x => x.CompletedAt).HasColumnName("completed_at").HasConversion(timestamp);
        delivery.Property(x => x.FailedAt).HasColumnName("failed_at").HasConversion(timestamp);
        delivery.Property(x => x.SupersededAt).HasColumnName("superseded_at").HasConversion(timestamp);
        delivery.Property(x => x.SupersededByDeliveryId).HasColumnName("superseded_by_delivery_id");
        delivery.Property(x => x.LeaseId).HasColumnName("lease_id");
        delivery.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at").HasConversion(timestamp);
        delivery.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        delivery.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_durable_mail_deliveries_status_next_attempt_at");
        delivery.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_durable_mail_deliveries_status_created_at");
        delivery.HasIndex(x => new { x.Status, x.LeaseExpiresAt }).HasDatabaseName("ix_durable_mail_deliveries_status_lease_expires_at");

        var attempt = modelBuilder.Entity<EmailDeliveryAttemptEntity>();
        attempt.ToTable("durable_mail_attempts");
        attempt.HasKey(x => x.Id).HasName("pk_durable_mail_attempts");
        attempt.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        attempt.Property(x => x.EmailDeliveryId).HasColumnName("email_delivery_id");
        attempt.Property(x => x.AttemptNumber).HasColumnName("attempt_number");
        attempt.Property(x => x.StartedAt).HasColumnName("started_at").HasConversion(timestamp);
        attempt.Property(x => x.FinishedAt).HasColumnName("finished_at").HasConversion(timestamp);
        attempt.Property(x => x.Outcome).HasColumnName("outcome").HasConversion<int>();
        attempt.Property(x => x.ErrorType).HasColumnName("error_type").HasMaxLength(512);
        attempt.Property(x => x.ErrorMessage).HasColumnName("error_message").HasMaxLength(4096);
        attempt.Property(x => x.StackTrace).HasColumnName("stack_trace").HasMaxLength(16384);
        attempt.HasIndex(x => new { x.EmailDeliveryId, x.AttemptNumber }).IsUnique()
            .HasDatabaseName("ix_durable_mail_attempts_delivery_attempt_number");
        attempt.HasOne<EmailDeliveryEntity>().WithMany().HasForeignKey(x => x.EmailDeliveryId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_durable_mail_attempts_delivery");

        var slot = modelBuilder.Entity<EmailDeliverySlotEntity>();
        slot.ToTable("durable_mail_slots");
        slot.HasKey(x => x.Key).HasName("pk_durable_mail_slots");
        slot.Property(x => x.Key).HasColumnName("key").HasMaxLength(256).ValueGeneratedNever();
        slot.Property(x => x.DeliveryId).HasColumnName("delivery_id");
        slot.Property(x => x.Source).HasColumnName("source").HasMaxLength(128);
        slot.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        slot.HasIndex(x => x.DeliveryId).HasDatabaseName("ix_durable_mail_slots_delivery_id");
        slot.HasOne<EmailDeliveryEntity>().WithMany().HasForeignKey(x => x.DeliveryId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_durable_mail_slots_delivery");
        return modelBuilder;
    }
}
