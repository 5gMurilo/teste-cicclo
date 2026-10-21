using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry;
using Cicclo.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cicclo.Api.Platform.Persistence.Configurations;

internal sealed class BookingItemConfiguration : IEntityTypeConfiguration<BookingItem>
{
    /// <summary>Sequência monotônica dos tickets de fila; gaps por rollback são normais e não quebram o FIFO.</summary>
    public const string QueueTicketSequence = "queue_ticket_seq";

    /// <summary>Uma atribuição ativa (oferta corrente ou execução) por máquina.</summary>
    public const string ActiveMachineIndex = "ux_service_request_items_active_machine";

    private static readonly ValueConverter<BookingItemStatus, string> StatusConverter =
        new(value => ToText(value), value => ParseStatus(value));

    public void Configure(EntityTypeBuilder<BookingItem> builder)
    {
        builder.ToTable("service_request_items", table =>
        {
            table.HasCheckConstraint("ck_service_request_items_unit_price_positive", "unit_price > 0");
            table.HasCheckConstraint(
                "ck_service_request_items_status",
                "status IN ('pending_activation', 'queued', 'to_be_confirmed', 'in_progress')");
            // Item ativado sempre tem ticket e queued_at; pending_activation nunca tem.
            table.HasCheckConstraint(
                "ck_service_request_items_queue_entry",
                "(status = 'pending_activation') = (queue_ticket IS NULL) AND (status = 'pending_activation') = (queued_at IS NULL)");
            // Máquina e prazo existem só na oferta corrente e na execução; o prazo é posterior à oferta, que não precede a fila.
            table.HasCheckConstraint(
                "ck_service_request_items_assignment",
                """
                (status IN ('to_be_confirmed', 'in_progress')) = (machine_id IS NOT NULL)
                AND (status IN ('to_be_confirmed', 'in_progress')) = (offered_at IS NOT NULL)
                AND (status IN ('to_be_confirmed', 'in_progress')) = (expires_at IS NOT NULL)
                AND (offered_at IS NULL OR (expires_at > offered_at AND offered_at >= queued_at))
                """);
            // Início só em execução, dentro da validade da oferta (now < expires_at).
            table.HasCheckConstraint(
                "ck_service_request_items_started",
                "(status = 'in_progress') = (started_at IS NOT NULL) AND (started_at IS NULL OR (started_at >= offered_at AND started_at < expires_at))");
        });

        builder.HasKey(item => item.Id).HasName("pk_service_request_items");
        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(item => item.BookingId).HasColumnName("request_id");
        builder.Property(item => item.ServiceCode).HasColumnName("service_code").HasColumnType("text").IsRequired();
        builder.Property(item => item.UnitPrice).HasColumnName("unit_price").HasPrecision(12, 2);
        builder.Property(item => item.Status).HasColumnName("status").HasColumnType("text").HasConversion(StatusConverter);
        builder.Property(item => item.QueueTicket).HasColumnName("queue_ticket");
        builder.Property(item => item.QueuedAt).HasColumnName("queued_at");
        builder.Property(item => item.MachineId).HasColumnName("machine_id");
        builder.Property(item => item.OfferedAt).HasColumnName("offered_at");
        builder.Property(item => item.ExpiresAt).HasColumnName("expires_at");
        builder.Property(item => item.StartedAt).HasColumnName("started_at");

        builder.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(item => item.BookingId)
            .HasConstraintName("fk_service_request_items_service_requests")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<LaundryService>()
            .WithMany()
            .HasForeignKey(item => item.ServiceCode)
            .HasConstraintName("fk_service_request_items_services")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Machine>()
            .WithMany()
            .HasForeignKey(item => item.MachineId)
            .HasConstraintName("fk_service_request_items_machines")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(item => item.BookingId).HasDatabaseName("ix_service_request_items_request_id");
        builder.HasIndex(item => item.ServiceCode).HasDatabaseName("ix_service_request_items_service_code");
        builder.HasIndex(item => item.MachineId)
            .IsUnique()
            .HasFilter("status IN ('to_be_confirmed', 'in_progress')")
            .HasDatabaseName(ActiveMachineIndex);
        builder.HasIndex(item => item.QueueTicket).IsUnique().HasDatabaseName("ux_service_request_items_queue_ticket");
        // Fila de um serviço: queued em ordem de ticket.
        builder.HasIndex(item => new { item.ServiceCode, item.Status, item.QueueTicket }).HasDatabaseName("ix_service_request_items_queue");
    }

    public static string ToText(BookingItemStatus value) => value switch
    {
        BookingItemStatus.PendingActivation => "pending_activation",
        BookingItemStatus.Queued => "queued",
        BookingItemStatus.ToBeConfirmed => "to_be_confirmed",
        BookingItemStatus.InProgress => "in_progress",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Estado de item sem representação persistida."),
    };

    public static BookingItemStatus ParseStatus(string value) => value switch
    {
        "pending_activation" => BookingItemStatus.PendingActivation,
        "queued" => BookingItemStatus.Queued,
        "to_be_confirmed" => BookingItemStatus.ToBeConfirmed,
        "in_progress" => BookingItemStatus.InProgress,
        _ => throw new InvalidOperationException($"Estado de item persistido desconhecido: '{value}'."),
    };
}
