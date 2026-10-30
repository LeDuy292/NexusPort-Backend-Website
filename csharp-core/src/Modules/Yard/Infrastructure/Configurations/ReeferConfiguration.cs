using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Yard.Domain.Entities.Reefer;

namespace NexusPort.Modules.Yard.Infrastructure.Configurations;

public class ReeferPlugConfiguration : IEntityTypeConfiguration<ReeferPlug>
{
    public void Configure(EntityTypeBuilder<ReeferPlug> builder)
    {
        builder.ToTable("reefer_plugs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SlotId).HasColumnName("slot_id");
        builder.Property(x => x.PlugCode).HasColumnName("plug_code").IsRequired();
        builder.Property(x => x.IsAvailable).HasColumnName("is_available").IsRequired();
        builder.Property(x => x.IsPowerOn).HasColumnName("is_power_on").IsRequired();
        builder.Property(x => x.UsageStatus).HasColumnName("usage_status").IsRequired();
        builder.Property(x => x.TechnicalStatus).HasColumnName("technical_status").IsRequired();
        builder.Property(x => x.VoltageType).HasColumnName("voltage_type");
        builder.Property(x => x.MaxPowerKw).HasColumnName("max_power_kw");
        builder.Property(x => x.LastMaintainedAt).HasColumnName("last_maintained_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        builder.HasMany(x => x.PowerSessions)
               .WithOne(x => x.Plug)
               .HasForeignKey(x => x.PlugId);
    }
}

public class ReeferPowerSessionConfiguration : IEntityTypeConfiguration<ReeferPowerSession>
{
    public void Configure(EntityTypeBuilder<ReeferPowerSession> builder)
    {
        builder.ToTable("reefer_power_sessions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContainerId).HasColumnName("container_id").IsRequired();
        builder.Property(x => x.PlugId).HasColumnName("plug_id").IsRequired();
        builder.Property(x => x.RequestId).HasColumnName("request_id");
        builder.Property(x => x.SessionStatus).HasColumnName("session_status").IsRequired();
        builder.Property(x => x.ActualPowerStatus).HasColumnName("actual_power_status").IsRequired();
        builder.Property(x => x.AssignedBy).HasColumnName("assigned_by");
        builder.Property(x => x.AssignedAt).HasColumnName("assigned_at").IsRequired();
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.EndedAt).HasColumnName("ended_at");
        builder.Property(x => x.PowerConfirmedBy).HasColumnName("power_confirmed_by");
        builder.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(x => x.LastCheckedAt).HasColumnName("last_checked_at");
        builder.Property(x => x.Note).HasColumnName("note");
        builder.Property(x => x.TerminationReason).HasColumnName("termination_reason");
        builder.Property(x => x.ParentSessionId).HasColumnName("parent_session_id");
        builder.Property(x => x.BillableHours).HasColumnName("billable_hours");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        builder.HasOne(x => x.Plug)
               .WithMany(x => x.PowerSessions)
               .HasForeignKey(x => x.PlugId);

        builder.HasOne(x => x.Request)
               .WithMany(x => x.Sessions)
               .HasForeignKey(x => x.RequestId)
               .IsRequired(false);

        builder.HasMany(x => x.TemperatureRecords)
               .WithOne(x => x.Session)
               .HasForeignKey(x => x.SessionId);

        builder.HasMany(x => x.Alerts)
               .WithOne(x => x.Session)
               .HasForeignKey(x => x.SessionId);
    }
}

public class ReeferTemperatureRecordConfiguration : IEntityTypeConfiguration<ReeferTemperatureRecord>
{
    public void Configure(EntityTypeBuilder<ReeferTemperatureRecord> builder)
    {
        builder.ToTable("reefer_temperature_records");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(x => x.SupplyTemperature).HasColumnName("supply_temperature");
        builder.Property(x => x.ReturnTemperature).HasColumnName("return_temperature").IsRequired();
        builder.Property(x => x.TemperatureUnit).HasColumnName("temperature_unit").IsRequired();
        builder.Property(x => x.AmbientTemperature).HasColumnName("ambient_temperature");
        builder.Property(x => x.DataSource).HasColumnName("data_source").IsRequired();
        builder.Property(x => x.IsAbnormal).HasColumnName("is_abnormal").IsRequired();
        builder.Property(x => x.RecordedBy).HasColumnName("recorded_by");
        builder.Property(x => x.DeviceId).HasColumnName("device_id");
        builder.Property(x => x.Notes).HasColumnName("notes");
        builder.Property(x => x.RecordedAt).HasColumnName("recorded_at").IsRequired();

        builder.HasOne(x => x.Session)
               .WithMany(x => x.TemperatureRecords)
               .HasForeignKey(x => x.SessionId);
    }
}

public class ReeferAlertConfiguration : IEntityTypeConfiguration<ReeferAlert>
{
    public void Configure(EntityTypeBuilder<ReeferAlert> builder)
    {
        builder.ToTable("reefer_alerts");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.AlertCode).HasColumnName("alert_code").IsRequired();
        builder.Property(x => x.SessionId).HasColumnName("session_id");
        builder.Property(x => x.PlugId).HasColumnName("plug_id");
        builder.Property(x => x.ContainerId).HasColumnName("container_id");
        builder.Property(x => x.AlertType).HasColumnName("alert_type").IsRequired();
        builder.Property(x => x.Severity).HasColumnName("severity").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.Message).HasColumnName("message").IsRequired();
        builder.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        builder.Property(x => x.AcknowledgedBy).HasColumnName("acknowledged_by");
        builder.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at");
        builder.Property(x => x.ResolvedBy).HasColumnName("resolved_by");
        builder.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
        builder.Property(x => x.ResolutionNotes).HasColumnName("resolution_notes");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne(x => x.Session)
               .WithMany(x => x.Alerts)
               .HasForeignKey(x => x.SessionId)
               .IsRequired(false);
    }
}

public class ReeferPowerRequestConfiguration : IEntityTypeConfiguration<ReeferPowerRequest>
{
    public void Configure(EntityTypeBuilder<ReeferPowerRequest> builder)
    {
        builder.ToTable("reefer_power_requests");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RequestNo).HasColumnName("request_no").IsRequired();
        builder.Property(x => x.ContainerId).HasColumnName("container_id").IsRequired();
        builder.Property(x => x.TargetTemperature).HasColumnName("target_temperature").IsRequired();
        builder.Property(x => x.TemperatureUnit).HasColumnName("temperature_unit").IsRequired();
        builder.Property(x => x.TempTolerance).HasColumnName("temp_tolerance").IsRequired();
        builder.Property(x => x.CargoDescription).HasColumnName("cargo_description");
        builder.Property(x => x.SpecialInstructions).HasColumnName("special_instructions");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.RequestedBy).HasColumnName("requested_by");
        builder.Property(x => x.CancelledReason).HasColumnName("cancelled_reason");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        builder.HasMany(x => x.Sessions)
               .WithOne(x => x.Request)
               .HasForeignKey(x => x.RequestId);
    }
}
