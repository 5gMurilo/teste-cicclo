using Cicclo.Api.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cicclo.Api.Platform.Persistence.Configurations;

internal sealed class WalletAccountConfiguration : IEntityTypeConfiguration<WalletAccount>
{
    public void Configure(EntityTypeBuilder<WalletAccount> builder)
    {
        builder.ToTable("wallets", table =>
        {
            table.HasCheckConstraint("ck_wallets_balance_non_negative", "balance >= 0");
            table.HasCheckConstraint("ck_wallets_currency_brl", "currency = 'BRL'");
        });

        builder.HasKey(wallet => wallet.Id).HasName("pk_wallets");
        builder.Property(wallet => wallet.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(wallet => wallet.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        builder.Property(wallet => wallet.Balance).HasColumnName("balance").HasPrecision(12, 2);
        builder.Property(wallet => wallet.CreatedAt).HasColumnName("created_at");
        builder.Property(wallet => wallet.UpdatedAt).HasColumnName("updated_at");
    }
}
