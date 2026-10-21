using Cicclo.Api.Bookings;
using Cicclo.Api.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cicclo.Api.Platform.Persistence.Configurations;

internal sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("service_requests", table =>
        {
            table.HasCheckConstraint("ck_service_requests_total_amount_positive", "total_amount > 0");
            table.HasCheckConstraint("ck_service_requests_request_fingerprint_sha256", "request_fingerprint ~ '^[0-9a-f]{64}$'");
        });

        builder.HasKey(booking => booking.Id).HasName("pk_service_requests");
        builder.Property(booking => booking.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(booking => booking.WalletId).HasColumnName("wallet_id");
        builder.Property(booking => booking.IdempotencyKey).HasColumnName("idempotency_key");
        builder.Property(booking => booking.RequestFingerprint).HasColumnName("request_fingerprint").HasColumnType("text").IsRequired();
        builder.Property(booking => booking.TotalAmount).HasColumnName("total_amount").HasPrecision(12, 2);
        builder.Property(booking => booking.CreatedAt).HasColumnName("created_at");

        builder.HasOne<WalletAccount>()
            .WithMany()
            .HasForeignKey(booking => booking.WalletId)
            .HasConstraintName("fk_service_requests_wallets")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(booking => new { booking.WalletId, booking.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_service_requests_wallet_idempotency_key");
    }
}
