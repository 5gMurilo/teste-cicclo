using Cicclo.Api.Bookings;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Laundry;
using Cicclo.Api.Wallet;

namespace Cicclo.Api.Tests.Integration.Bookings;

/// <summary>
/// Envolve o adaptador PostgreSQL real para pausar a transação em pontos exatos ou injetar falhas.
/// Locks, débito e rollback continuam sendo do banco real; o decorador nunca simula persistência.
/// </summary>
public sealed class ScriptedBookingSessions(ICreateBookingSessions inner) : ICreateBookingSessions
{
    private int opened;

    public int Opened => Volatile.Read(ref opened);

    /// <summary>Executado com a wallet já bloqueada; recebe o número da sessão (1 = primeira).</summary>
    public Func<int, Task>? AfterLockWallet { get; init; }

    /// <summary>Executado com serviços e máquinas já bloqueados.</summary>
    public Func<int, Task>? AfterLockCatalog { get; init; }

    /// <summary>Executado depois do débito real, ainda dentro da transação.</summary>
    public Func<int, Task>? AfterDebit { get; init; }

    /// <summary>Substitui o resultado real da busca pela chave.</summary>
    public Func<int, StoredBooking?, StoredBooking?>? OnFind { get; init; }

    /// <summary>Substitui o commit; recebe o commit real para chamá-lo ou não.</summary>
    public Func<int, Func<Task>, Task>? OnCommit { get; init; }

    public List<Exception> Failures { get; } = [];

    public async Task<ICreateBookingSession> OpenAsync(CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref opened);
        return new Session(this, number, await inner.OpenAsync(cancellationToken));
    }

    private sealed class Session(ScriptedBookingSessions script, int number, ICreateBookingSession inner) : ICreateBookingSession
    {
        public IQueueSession Queue => inner.Queue;

        public Task<decimal> LockWalletAsync(Guid walletId, CancellationToken cancellationToken) => Observe(async () =>
        {
            var balance = await inner.LockWalletAsync(walletId, cancellationToken);
            await (script.AfterLockWallet?.Invoke(number) ?? Task.CompletedTask);
            return balance;
        });

        public Task<StoredBooking?> FindByKeyAsync(Guid walletId, Guid idempotencyKey, CancellationToken cancellationToken) => Observe(async () =>
        {
            var found = await inner.FindByKeyAsync(walletId, idempotencyKey, cancellationToken);
            return script.OnFind is null ? found : script.OnFind(number, found);
        });

        public Task<IReadOnlyList<CatalogEntry>> LockCatalogAsync(IReadOnlyCollection<string> serviceCodes, CancellationToken cancellationToken) =>
            Observe(async () =>
            {
                var catalog = await inner.LockCatalogAsync(serviceCodes, cancellationToken);
                await (script.AfterLockCatalog?.Invoke(number) ?? Task.CompletedTask);
                return catalog;
            });

        public Task<decimal?> TryDebitAsync(Guid walletId, decimal amount, DateTime updatedAt, CancellationToken cancellationToken) => Observe(async () =>
        {
            var balance = await inner.TryDebitAsync(walletId, amount, updatedAt, cancellationToken);
            await (script.AfterDebit?.Invoke(number) ?? Task.CompletedTask);
            return balance;
        });

        public Task AddAsync(Booking booking, IReadOnlyList<BookingItem> items, LedgerEntry debit, CancellationToken cancellationToken) =>
            Observe(async () =>
            {
                await inner.AddAsync(booking, items, debit, cancellationToken);
                return true;
            });

        public Task CommitAsync() => Observe(async () =>
        {
            await (script.OnCommit is null ? inner.CommitAsync() : script.OnCommit(number, inner.CommitAsync));
            return true;
        });

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private async Task<T> Observe<T>(Func<Task<T>> operation)
        {
            try
            {
                return await operation();
            }
            catch (Exception exception)
            {
                lock (script.Failures)
                {
                    script.Failures.Add(exception);
                }

                throw;
            }
        }
    }
}
