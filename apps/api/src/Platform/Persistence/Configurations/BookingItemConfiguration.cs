using Cicclo.Api.Bookings;
using Cicclo.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cicclo.Api.Platform.Persistence.Configurations;

internal sealed class BookingItemConfiguration : IEntityTypeConfiguration<BookingItem>
{
    private static readonly ValueConverter<BookingItemStatus, string> StatusConverter =
        new(value => ToText(value), value => ParseStatus(value));

    public void Configure(EntityTypeBuilder<BookingItem> builder)
    {
        builder.ToTable("service_request_items", table =>
        {
            table.HasCheckConstraint("ck_service_request_items_unit_price_positive", "unit_price > 0");
            table.HasCheckConstraint("ck_service_request_items_status", "status IN ('pending_activation')");
        });

        builder.HasKey(item => item.Id).HasName("pk_service_request_items");
        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(item => item.BookingId).HasColumnName("request_id");
        builder.Property(item => item.ServiceCode).HasColumnName("service_code").HasColumnType("text").IsRequired();
        builder.Property(item => item.UnitPrice).HasColumnName("unit_price").HasPrecision(12, 2);
        builder.Property(item => item.Status).HasColumnName("status").HasColumnType("text").HasConversion(StatusConverter);

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

        builder.HasIndex(item => item.BookingId).HasDatabaseName("ix_service_request_items_request_id");
        builder.HasIndex(item => item.ServiceCode).HasDatabaseName("ix_service_request_items_service_code");
    }

    public static string ToText(BookingItemStatus value) => value switch
    {
        BookingItemStatus.PendingActivation => "pending_activation",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Estado de item sem representação persistida."),
    };

    private static BookingItemStatus ParseStatus(string value) => value switch
    {
        "pending_activation" => BookingItemStatus.PendingActivation,
        _ => throw new InvalidOperationException($"Estado de item persistido desconhecido: '{value}'."),
    };
}
