using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Yard.Preparation.Domain;

namespace NexusPort.Modules.Yard.Preparation.Infrastructure.Configurations;

public class PreparationTaskConfiguration : IEntityTypeConfiguration<PreparationTask>
{
    public void Configure(EntityTypeBuilder<PreparationTask> builder)
    {
        builder.ToTable("preparation_tasks");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ServiceOrderId).HasColumnName("service_order_id");
        builder.Property(x => x.SlotBookingId).HasColumnName("slot_booking_id").IsRequired();
        builder.Property(x => x.ContainerNo).HasColumnName("container_no").HasMaxLength(50).IsRequired();
        builder.Property(x => x.TargetReadyAt).HasColumnName("target_ready_at").IsRequired();
        builder.Property(x => x.StartBy).HasColumnName("start_by").IsRequired();
        builder.Property(x => x.Priority).HasColumnName("priority").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.PlannedRehandleCount).HasColumnName("planned_rehandle_count").IsRequired();
        builder.Property(x => x.ActualRehandleCount).HasColumnName("actual_rehandle_count").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ReadyAt).HasColumnName("ready_at");
        builder.Property(x => x.CancelReason).HasColumnName("cancel_reason").HasMaxLength(500);
        builder.Property(x => x.Note).HasColumnName("note").HasMaxLength(1000);
        builder.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

        // Base entity unused properties
        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);





        // Outbound & metadata properties (persisted via Note envelope)
        builder.Ignore(x => x.Direction);
        builder.Ignore(x => x.VoyageId);
        builder.Ignore(x => x.VesselName);
        builder.Ignore(x => x.VoyageCode);
        builder.Ignore(x => x.StowagePosition);
        builder.Ignore(x => x.LoadingSequence);
        builder.Ignore(x => x.QuayCraneCode);
        builder.Ignore(x => x.Pod);
        builder.Ignore(x => x.VgmWeight);
        builder.Ignore(x => x.IsVgmVerified);
        builder.Ignore(x => x.IsCustomsCleared);
        builder.Ignore(x => x.HasHoldFlag);

        builder.HasIndex(x => new { x.SlotBookingId, x.ContainerNo });
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.TargetReadyAt);

        builder.HasMany(x => x.WorkOrders)
               .WithOne(w => w.Task)
               .HasForeignKey(w => w.PreparationTaskId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

public class PreparationWorkOrderConfiguration : IEntityTypeConfiguration<PreparationWorkOrder>
{
    public void Configure(EntityTypeBuilder<PreparationWorkOrder> builder)
    {
        builder.ToTable("preparation_work_orders");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.PreparationTaskId).HasColumnName("preparation_task_id").IsRequired();
        builder.Property(x => x.YardTaskId).HasColumnName("yard_task_id");
        builder.Property(x => x.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.Sequence).HasColumnName("sequence").IsRequired();
        builder.Property(x => x.ContainerNo).HasColumnName("container_no").HasMaxLength(50).IsRequired();
        builder.Property(x => x.FromSlotId).HasColumnName("from_slot_id").IsRequired();
        builder.Property(x => x.ToSlotId).HasColumnName("to_slot_id").IsRequired();
        builder.Property(x => x.EquipmentId).HasColumnName("equipment_id");
        builder.Property(x => x.AssigneeId).HasColumnName("assignee_id");
        builder.Property(x => x.Priority).HasColumnName("priority").IsRequired();
        builder.Property(x => x.DueAt).HasColumnName("due_at").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.FailReason).HasColumnName("fail_reason").HasMaxLength(500);
        builder.Property(x => x.ChargeParty).HasColumnName("charge_party").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.SelectionReason).HasColumnName("selection_reason").HasMaxLength(500);
        builder.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);





        builder.HasOne(x => x.FromSlot)
               .WithMany()
               .HasForeignKey(x => x.FromSlotId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ToSlot)
               .WithMany()
               .HasForeignKey(x => x.ToSlotId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.PreparationTaskId);
        builder.HasIndex(x => x.YardTaskId);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.AssigneeId);
        builder.HasIndex(x => x.EquipmentId);
    }
}

public class SlotReservationConfiguration : IEntityTypeConfiguration<SlotReservation>
{
    public void Configure(EntityTypeBuilder<SlotReservation> builder)
    {
        builder.ToTable("slot_reservations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SlotId).HasColumnName("slot_id").IsRequired();
        builder.Property(x => x.WorkOrderId).HasColumnName("work_order_id").IsRequired();
        builder.Property(x => x.ReservedUntil).HasColumnName("reserved_until").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);





        // One slot can only have one active reservation
        builder.HasIndex(x => x.SlotId).IsUnique();

        builder.HasOne(x => x.Slot)
               .WithMany()
               .HasForeignKey(x => x.SlotId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

public class PreparationConfigConfiguration : IEntityTypeConfiguration<PreparationConfig>
{
    public void Configure(EntityTypeBuilder<PreparationConfig> builder)
    {
        builder.ToTable("preparation_configs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.LeadTimeMinutes).HasColumnName("lead_time_minutes").IsRequired();
        builder.Property(x => x.MaxRehandlePerTask).HasColumnName("max_rehandle_per_task").IsRequired();
        builder.Property(x => x.StagingBlockId).HasColumnName("staging_block_id");
        builder.Property(x => x.RescheduleGraceMinutes).HasColumnName("reschedule_grace_minutes").IsRequired();
        builder.Property(x => x.DelayCheckIntervalSeconds).HasColumnName("delay_check_interval_seconds").IsRequired();
        builder.Property(x => x.EstimatedMinutesPerMove).HasColumnName("estimated_minutes_per_move").IsRequired();
        builder.Property(x => x.WeightDueNear).HasColumnName("weight_due_near").IsRequired();
        builder.Property(x => x.WeightReefer).HasColumnName("weight_reefer").IsRequired();
        builder.Property(x => x.WeightExpiringFreeTime).HasColumnName("weight_expiring_free_time").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        builder.Ignore(x => x.OutboundLeadTimeMinutes);
        builder.Ignore(x => x.ReeferMaxUnplugMinutes);
        builder.Ignore(x => x.ClosingTimeGraceMinutes);
        builder.Ignore(x => x.MarshallingMode);




    }
}

public class ContainerMovementConfiguration : IEntityTypeConfiguration<ContainerMovement>
{
    public void Configure(EntityTypeBuilder<ContainerMovement> builder)
    {
        builder.ToTable("container_movements");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContainerNo).HasColumnName("container_no").HasMaxLength(50).IsRequired();
        builder.Property(x => x.FromSlotId).HasColumnName("from_slot_id").IsRequired();
        builder.Property(x => x.ToSlotId).HasColumnName("to_slot_id").IsRequired();
        builder.Property(x => x.MovedBy).HasColumnName("moved_by");
        builder.Property(x => x.EquipmentId).HasColumnName("equipment_id");
        builder.Property(x => x.MovedAt).HasColumnName("moved_at").IsRequired();
        builder.Property(x => x.WorkOrderId).HasColumnName("work_order_id");
        builder.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);





        builder.HasIndex(x => x.ContainerNo);
        builder.HasIndex(x => x.MovedAt);
    }
}

public class EDoRecordConfiguration : IEntityTypeConfiguration<EDoRecord>
{
    public void Configure(EntityTypeBuilder<EDoRecord> builder)
    {
        builder.ToTable("edo_records");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContainerNo).HasColumnName("container_no").HasMaxLength(50).IsRequired();
        builder.Property(x => x.SealNo).HasColumnName("seal_no").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ReleaseStatus).HasColumnName("release_status").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.HoldFlag).HasColumnName("hold_flag").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(x => x.FreeTimeUntil).HasColumnName("free_time_until");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);





        builder.HasIndex(x => x.ContainerNo).IsUnique();
    }
}

public class ExceptionItemConfiguration : IEntityTypeConfiguration<ExceptionItem>
{
    public void Configure(EntityTypeBuilder<ExceptionItem> builder)
    {
        builder.ToTable("exception_items");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Category).HasColumnName("category").HasMaxLength(50).IsRequired();
        builder.Property(x => x.EntityId).HasColumnName("entity_id");
        builder.Property(x => x.ContainerNo).HasColumnName("container_no").HasMaxLength(50);
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Severity).HasColumnName("severity").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
        builder.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(1000);

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);





        builder.HasIndex(x => x.Category);
        builder.HasIndex(x => x.Status);
    }
}

public class PreparationOutboxMessageConfiguration : IEntityTypeConfiguration<PreparationOutboxMessage>
{
    public void Configure(EntityTypeBuilder<PreparationOutboxMessage> builder)
    {
        builder.ToTable("preparation_outbox_messages");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.EventId).HasColumnName("event_id").HasMaxLength(64).IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Payload).HasColumnName("payload").IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(x => x.PublishedAt).HasColumnName("published_at");
        builder.Property(x => x.RetryCount).HasColumnName("retry_count").IsRequired();
        builder.Property(x => x.Error).HasColumnName("error").HasMaxLength(1000);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);





        builder.HasIndex(x => new { x.PublishedAt, x.RetryCount });
    }
}

public class SlotBookingConfiguration : IEntityTypeConfiguration<SlotBooking>
{
    public void Configure(EntityTypeBuilder<SlotBooking> builder)
    {
        builder.ToView("bookings");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BookingCode).HasColumnName("booking_code").HasMaxLength(80);
        builder.Property(x => x.AppointmentStart).HasColumnName("appointment_start");
        builder.Property(x => x.AppointmentEnd).HasColumnName("appointment_end");
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>();

        builder.Ignore(x => x.CreatedAt);
        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);




    }
}
