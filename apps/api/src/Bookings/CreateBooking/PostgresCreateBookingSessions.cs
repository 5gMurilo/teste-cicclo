using System.Data;
using Cicclo.Api.Laundry;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Platform.Persistence.Configurations;
using Cicclo.Api.Services;
using Cicclo.Api.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Cicclo.Api.Bookings.CreateBooking;

/// <summary>
/// Adaptador PostgreSQL da compra: um DbContext e uma transação Read Committed por sessão, locks explícitos
/// (FOR UPDATE) na ordem wallet → serviços por código → máquinas por tipo/id e SQL parametrizado.
/// </summary>
public sealed class PostgresCreateBookingSessions(IDbContextFactory<CiccloDbContext> contexts) : ICreateBookingSessions
{
    public const string IdempotencyKeyConstraint = "ux_service_requests_wallet_idempotency_key";

    public async Task<ICreateBookingSession> OpenAsync(CancellationToken cancellationToken)
    {
        var db = await contexts.CreateDbContextAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(db.Database.GetConnectionString()))
            {
                throw new DatabaseUnavailableException("Conexão com o PostgreSQL não configurada.");
            }

            var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            return new Session(db, transaction);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private sealed class Session(CiccloDbContext db, IDbContextTransaction transaction) : ICreateBookingSession
    {
        private static readonly string[] OperationalStates = [.. ServiceAvailability.OperationalStates.Select(LaundryColumns.ToText)];

        private readonly PostgresQueueSession queue = new(db);

        /// <summary>Itens inseridos nesta transação: seu booking confirma na revisão inicial mesmo com a ativação deles.</summary>
        private readonly List<Guid> createdItemIds = [];

        public IQueueSession Queue => queue;

        public Task<decimal> LockWalletAsync(Guid walletId, CancellationToken cancellationToken) => Translate(async () =>
        {
            var balances = await db.Database
                .SqlQuery<decimal>($"SELECT balance AS \"Value\" FROM wallets WHERE id = {walletId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            return balances.Count == 1 ? balances[0] : throw new DatabaseUnavailableException("Wallet demo ausente: execute o bootstrap.");
        });

        public Task<StoredBooking?> FindByKeyAsync(Guid walletId, Guid idempotencyKey, CancellationToken cancellationToken) => Translate(async () =>
        {
            var booking = await db.Bookings
                .AsNoTracking()
                .Where(row => row.WalletId == walletId && row.IdempotencyKey == idempotencyKey)
                .SingleOrDefaultAsync(cancellationToken);
            if (booking is null)
            {
                return null;
            }

            var items = await db.BookingItems
                .AsNoTracking()
                .Where(item => item.BookingId == booking.Id)
                .Select(item => new BookingItemSnapshot(item.Id, item.ServiceCode, item.UnitPrice, item.Status))
                .ToListAsync(cancellationToken);
            return new StoredBooking(booking.Id, booking.RequestFingerprint, booking.TotalAmount, booking.CreatedAt, items);
        });

        public Task<IReadOnlyList<CatalogEntry>> LockCatalogAsync(IReadOnlyCollection<string> serviceCodes, CancellationToken cancellationToken) =>
            Translate<IReadOnlyList<CatalogEntry>>(async () =>
            {
                var codes = serviceCodes.ToArray();
                var services = await db.Database
                    .SqlQuery<ServiceRow>(
                        $"""
                        SELECT code AS "Code", current_price AS "CurrentPrice", is_active AS "IsActive", machine_type AS "MachineType"
                        FROM services WHERE code = ANY({codes}) ORDER BY code COLLATE "C" FOR UPDATE
                        """)
                    .ToListAsync(cancellationToken);
                var types = services.Select(service => service.MachineType).Distinct(StringComparer.Ordinal).ToArray();
                var machines = await db.Database
                    .SqlQuery<MachineRow>(
                        $"""
                        SELECT machine_type AS "MachineType", state AS "State"
                        FROM machines WHERE machine_type = ANY({types}) ORDER BY machine_type COLLATE "C", id FOR UPDATE
                        """)
                    .ToListAsync(cancellationToken);
                return
                [
                    .. services.Select(service => new CatalogEntry(
                        service.Code,
                        service.CurrentPrice,
                        service.IsActive,
                        machines.Any(machine => machine.MachineType == service.MachineType && OperationalStates.Contains(machine.State)))),
                ];
            });

        // SPEC_DEVIATION: o débito condicional fica nesta sessão de compra, não num adaptador próprio de Wallet.
        // Reason: uma sessão/transação por comando; um adaptador separado exigiria repartir DbContext e transação
        // entre portas sem um segundo consumidor (cancelamento/refund chegam em E05 e reavaliam a extração).
        public Task<decimal?> TryDebitAsync(Guid walletId, decimal amount, DateTime updatedAt, CancellationToken cancellationToken) => Translate(async () =>
        {
            var balances = await db.Database
                .SqlQuery<decimal>(
                    $"""
                    UPDATE wallets SET balance = balance - {amount}, updated_at = {updatedAt}
                    WHERE id = {walletId} AND balance >= {amount}
                    RETURNING balance AS "Value"
                    """)
                .ToListAsync(cancellationToken);
            return balances.Count == 1 ? balances[0] : (decimal?)null;
        });

        public Task AddAsync(Booking booking, IReadOnlyList<BookingItem> items, LedgerEntry debit, CancellationToken cancellationToken) => Translate(async () =>
        {
            db.Bookings.Add(booking);
            createdItemIds.AddRange(items.Select(item => item.Id));
            db.BookingItems.AddRange(items);
            db.LedgerEntries.Add(debit);
            return await db.SaveChangesAsync(cancellationToken);
        });

        public IReadOnlyList<RevisedBooking> CommittedRevisions { get; private set; } = [];

        public async Task CommitAsync()
        {
            // Bookings de outras compras alterados pela ativação (oferta vencida devolvida à fila) recebem a revisão
            // nesta mesma transação; uma falha aqui ainda é rollback conhecido, não commit desconhecido.
            var revised = await Translate(() => queue.IncrementRevisionsAsync(createdItemIds, CancellationToken.None));
            try
            {
                // Sem token: cancelar no meio do commit só tornaria o resultado desconhecido.
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                throw new CreateBookingStoreException(
                    CreateBookingStoreFailure.CommitOutcomeUnknown, "Commit da compra sem confirmação do PostgreSQL.", exception);
            }

            CommittedRevisions = revised;
        }

        public async ValueTask DisposeAsync()
        {
            // Sem commit, descartar a transação faz rollback; não depende do token da requisição.
            await transaction.DisposeAsync();
            await db.DisposeAsync();
        }

        private static async Task<T> Translate<T>(Func<Task<T>> operation)
        {
            try
            {
                return await operation();
            }
            catch (Exception exception) when (FindPostgres(exception) is { } postgres && Classify(postgres) is { } failure)
            {
                throw new CreateBookingStoreException(failure, "Transação de compra abortada pelo PostgreSQL.", exception);
            }
        }

        private static CreateBookingStoreFailure? Classify(PostgresException exception) => exception.SqlState switch
        {
            PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure => CreateBookingStoreFailure.RetryableConflict,
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == IdempotencyKeyConstraint => CreateBookingStoreFailure.IdempotencyKeyTaken,
            _ => null,
        };

        private static PostgresException? FindPostgres(Exception? exception)
        {
            for (; exception is not null; exception = exception.InnerException)
            {
                if (exception is PostgresException postgres)
                {
                    return postgres;
                }
            }

            return null;
        }
    }

    private sealed class ServiceRow
    {
        public string Code { get; init; } = "";

        public decimal CurrentPrice { get; init; }

        public bool IsActive { get; init; }

        public string MachineType { get; init; } = "";
    }

    private sealed class MachineRow
    {
        public string MachineType { get; init; } = "";

        public string State { get; init; } = "";
    }
}
