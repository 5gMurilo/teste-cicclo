using Cicclo.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cicclo.Api.Platform.Persistence.Configurations;

internal sealed class LaundryServiceConfiguration : IEntityTypeConfiguration<LaundryService>
{
    public void Configure(EntityTypeBuilder<LaundryService> builder)
    {
        builder.ToTable("services", table =>
        {
            table.HasCheckConstraint("ck_services_current_price_positive", "current_price > 0");
            table.HasCheckConstraint(
                "ck_services_code_machine_type",
                "(code = 'wash' AND machine_type = 'washer') OR (code = 'dry' AND machine_type = 'dryer')");
        });

        builder.HasKey(service => service.Code).HasName("pk_services");
        builder.Property(service => service.Code).HasColumnName("code").HasColumnType("text");
        builder.Property(service => service.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        builder.Property(service => service.MachineType)
            .HasColumnName("machine_type")
            .HasColumnType("text")
            .HasConversion(LaundryColumns.MachineTypeConverter);
        builder.Property(service => service.CurrentPrice).HasColumnName("current_price").HasPrecision(12, 2);
        builder.Property(service => service.IsActive).HasColumnName("is_active");
        builder.Property(service => service.CreatedAt).HasColumnName("created_at");
    }
}
