using Cicclo.Api.Laundry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cicclo.Api.Platform.Persistence.Configurations;

internal sealed class MachineConfiguration : IEntityTypeConfiguration<Machine>
{
    public void Configure(EntityTypeBuilder<Machine> builder)
    {
        builder.ToTable("machines", table =>
        {
            table.HasCheckConstraint("ck_machines_machine_type", "machine_type IN ('washer', 'dryer')");
            table.HasCheckConstraint(
                "ck_machines_state",
                "state IN ('available', 'reserved', 'running', 'out_of_service')");
        });

        builder.HasKey(machine => machine.Id).HasName("pk_machines");
        builder.Property(machine => machine.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(machine => machine.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        builder.Property(machine => machine.MachineType)
            .HasColumnName("machine_type")
            .HasColumnType("text")
            .HasConversion(LaundryColumns.MachineTypeConverter);
        builder.Property(machine => machine.State)
            .HasColumnName("state")
            .HasColumnType("text")
            .HasConversion(LaundryColumns.MachineStateConverter);
        builder.Property(machine => machine.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(machine => machine.Name).IsUnique().HasDatabaseName("ux_machines_name");
        builder.HasIndex(machine => new { machine.MachineType, machine.State }).HasDatabaseName("ix_machines_machine_type_state");
    }
}
