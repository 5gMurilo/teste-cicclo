using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cicclo.Api.Platform.Persistence.Configurations;

internal sealed class MachineIncidentConfiguration : IEntityTypeConfiguration<MachineIncident>
{
    /// <summary>Um relato aberto (reported) por item: defesa no banco além da serialização pelo lock da wallet.</summary>
    public const string OpenReportPerItemIndex = "ux_machine_incidents_item_reported";

    private static readonly ValueConverter<IncidentStatus, string> StatusConverter =
        new(value => ToText(value), value => ParseStatus(value));

    public void Configure(EntityTypeBuilder<MachineIncident> builder)
    {
        builder.ToTable("machine_incidents", table =>
        {
            table.HasCheckConstraint("ck_machine_incidents_status", "status IN ('reported', 'confirmed')");
            // Descrição ausente é null; presente, já sem espaços nas pontas e com 1 a 1000 caracteres.
            table.HasCheckConstraint("ck_machine_incidents_description", "description IS NULL OR char_length(description) BETWEEN 1 AND 1000");
        });

        builder.HasKey(incident => incident.Id).HasName("pk_machine_incidents");
        builder.Property(incident => incident.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(incident => incident.MachineId).HasColumnName("machine_id");
        builder.Property(incident => incident.BookingItemId).HasColumnName("request_item_id");
        builder.Property(incident => incident.Status).HasColumnName("status").HasColumnType("text").HasConversion(StatusConverter);
        builder.Property(incident => incident.Description).HasColumnName("description").HasColumnType("text");
        builder.Property(incident => incident.ReportedAt).HasColumnName("reported_at");

        builder.HasOne<Machine>()
            .WithMany()
            .HasForeignKey(incident => incident.MachineId)
            .HasConstraintName("fk_machine_incidents_machines")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BookingItem>()
            .WithMany()
            .HasForeignKey(incident => incident.BookingItemId)
            .HasConstraintName("fk_machine_incidents_service_request_items")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(incident => incident.MachineId).HasDatabaseName("ix_machine_incidents_machine_id");
        builder.HasIndex(incident => incident.BookingItemId, OpenReportPerItemIndex)
            .IsUnique()
            .HasFilter("status = 'reported'");
    }

    public static string ToText(IncidentStatus value) => value switch
    {
        IncidentStatus.Reported => "reported",
        IncidentStatus.Confirmed => "confirmed",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Estado de incidente sem representação persistida."),
    };

    private static IncidentStatus ParseStatus(string value) => value switch
    {
        "reported" => IncidentStatus.Reported,
        "confirmed" => IncidentStatus.Confirmed,
        _ => throw new InvalidOperationException($"Estado de incidente persistido desconhecido: '{value}'."),
    };
}
