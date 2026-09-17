using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Carrier.Domain.Entities;

namespace NexusPort.Modules.Carrier.Infrastructure.Persistence;

public class CarrierConfiguration : IEntityTypeConfiguration<CarrierEntity>
{
    public void Configure(EntityTypeBuilder<CarrierEntity> builder)
    {
        builder.ToTable("carriers");
        builder.HasKey(c => c.Id);
        
        builder.Property(c => c.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(c => c.CompanyName).HasColumnName("company_name").IsRequired();
        builder.Property(c => c.TaxCode).HasColumnName("tax_code");
        builder.Property(c => c.Address).HasColumnName("address");
        builder.Property(c => c.Phone).HasColumnName("phone");
        builder.Property(c => c.Email).HasColumnName("email");
        builder.Property(c => c.ContactPerson).HasColumnName("contact_person");
        builder.Property(x => x.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasDefaultValue(NexusPort.Modules.Carrier.Domain.Enums.CompanyStatus.inactive);
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Ignore(c => c.CreatedBy);
        builder.Ignore(c => c.UpdatedAt);
        builder.Ignore(c => c.UpdatedBy);
        builder.Ignore(c => c.IsDeleted);
    }
}
