using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry;
using Cicclo.Api.Services;
using Cicclo.Api.Wallet;
using Microsoft.EntityFrameworkCore;

namespace Cicclo.Api.Platform.Persistence;

/// <summary>Unidade de persistência única da API; usada somente por adaptadores e pelo bootstrap.</summary>
public sealed class CiccloDbContext(DbContextOptions<CiccloDbContext> options) : DbContext(options)
{
    public const string ConnectionStringName = "Cicclo";

    public DbSet<WalletAccount> Wallets => Set<WalletAccount>();

    public DbSet<LaundryService> Services => Set<LaundryService>();

    public DbSet<Machine> Machines => Set<Machine>();

    public DbSet<Booking> Bookings => Set<Booking>();

    public DbSet<BookingItem> BookingItems => Set<BookingItem>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(Configurations.BookingItemConfiguration.QueueTicketSequence);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CiccloDbContext).Assembly);
    }
}
