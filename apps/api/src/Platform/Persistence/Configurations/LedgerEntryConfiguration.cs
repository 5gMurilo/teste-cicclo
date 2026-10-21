using Cicclo.Api.Bookings;
using Cicclo.Api.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cicclo.Api.Platform.Persistence.Configurations;

internal sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    private static readonly ValueConverter<LedgerEntryKind, string> KindConverter =
        new(value => ToText(value), value => ParseKind(value));

    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("wallet_transactions", table =>
        {
            table.HasCheckConstraint("ck_wallet_transactions_kind", "kind IN ('debit')");
            table.HasCheckConstraint("ck_wallet_transactions_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_wallet_transactions_balance_after_non_negative", "balance_after >= 0");
            table.HasCheckConstraint("ck_wallet_transactions_debit_without_item", "kind <> 'debit' OR request_item_id IS NULL");
        });

        builder.HasKey(entry => entry.Id).HasName("pk_wallet_transactions");
        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(entry => entry.WalletId).HasColumnName("wallet_id");
        builder.Property(entry => entry.BookingId).HasColumnName("request_id");
        builder.Property(entry => entry.BookingItemId).HasColumnName("request_item_id");
        builder.Property(entry => entry.Kind).HasColumnName("kind").HasColumnType("text").HasConversion(KindConverter);
        builder.Property(entry => entry.Amount).HasColumnName("amount").HasPrecision(12, 2);
        builder.Property(entry => entry.BalanceAfter).HasColumnName("balance_after").HasPrecision(12, 2);
        builder.Property(entry => entry.CreatedAt).HasColumnName("created_at");

        builder.HasOne<WalletAccount>()
            .WithMany()
            .HasForeignKey(entry => entry.WalletId)
            .HasConstraintName("fk_wallet_transactions_wallets")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(entry => entry.BookingId)
            .HasConstraintName("fk_wallet_transactions_service_requests")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BookingItem>()
            .WithMany()
            .HasForeignKey(entry => entry.BookingItemId)
            .HasConstraintName("fk_wallet_transactions_service_request_items")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(entry => entry.WalletId).HasDatabaseName("ix_wallet_transactions_wallet_id");
        builder.HasIndex(entry => entry.BookingItemId).HasDatabaseName("ix_wallet_transactions_request_item_id");
        // Um único débito por booking; também atende a FK request_id.
        builder.HasIndex(entry => entry.BookingId)
            .IsUnique()
            .HasFilter("kind = 'debit'")
            .HasDatabaseName("ux_wallet_transactions_request_debit");
    }

    public static string ToText(LedgerEntryKind value) => value switch
    {
        LedgerEntryKind.Debit => "debit",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Tipo de lançamento sem representação persistida."),
    };

    private static LedgerEntryKind ParseKind(string value) => value switch
    {
        "debit" => LedgerEntryKind.Debit,
        _ => throw new InvalidOperationException($"Tipo de lançamento persistido desconhecido: '{value}'."),
    };
}
