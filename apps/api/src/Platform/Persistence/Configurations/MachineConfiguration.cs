using Cicclo.Api.Bookings;
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
            // Item ativo só em reserved/running; reserved é sempre uma oferta a um item.
            table.HasCheckConstraint(
                "ck_machines_active_item",
                "(state IN ('reserved', 'running') OR active_item_id IS NULL) AND (state <> 'reserved' OR active_item_id IS NOT NULL)");
            // Running sem item é sempre ocupação sintética, e a marca sintética só existe nesse caso: execução real
            // tem item, e available/reserved/out_of_service nunca são sintéticas.
            table.HasCheckConstraint(
                "ck_machines_demo_occupied",
                "is_demo_occupied = (state = 'running' AND active_item_id IS NULL)");
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
        builder.Property(machine => machine.ActiveItemId).HasColumnName("active_item_id");
        builder.Property(machine => machine.IsDemoOccupied).HasColumnName("is_demo_occupied").HasDefaultValue(false);

        builder.HasOne<BookingItem>()
            .WithMany()
            .HasForeignKey(machine => machine.ActiveItemId)
            .HasConstraintName("fk_machines_service_request_items")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(machine => machine.Name).IsUnique().HasDatabaseName("ux_machines_name");
        builder.HasIndex(machine => new { machine.MachineType, machine.State }).HasDatabaseName("ix_machines_machine_type_state");
        builder.HasIndex(machine => machine.ActiveItemId).IsUnique().HasDatabaseName("ux_machines_active_item_id");
    }
}
