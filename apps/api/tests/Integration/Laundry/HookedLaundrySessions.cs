using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry;

namespace Cicclo.Api.Tests.Integration.Laundry;

/// <summary>
/// Envolve o adaptador PostgreSQL real e executa um sinal imediatamente antes do commit verdadeiro (por exemplo,
/// derrubar a conexão no servidor). Locks, mutações e commit continuam sendo do banco; nada é simulado.
/// </summary>
internal sealed class HookedLaundrySessions(ILaundrySessions inner, Func<Task> beforeCommit) : ILaundrySessions
{
    public async Task<ILaundrySession> OpenAsync(CancellationToken cancellationToken) =>
        new Session(await inner.OpenAsync(cancellationToken), beforeCommit);

    private sealed class Session(ILaundrySession inner, Func<Task> beforeCommit) : ILaundrySession
    {
        public Task LockWalletAsync(Guid walletId, CancellationToken cancellationToken) => inner.LockWalletAsync(walletId, cancellationToken);

        public Task<ItemLocation?> LocateItemAsync(Guid itemId, CancellationToken cancellationToken) => inner.LocateItemAsync(itemId, cancellationToken);

        public Task<IReadOnlyList<MachineSlot>> LockMachinesAsync(MachineType type, CancellationToken cancellationToken) =>
            inner.LockMachinesAsync(type, cancellationToken);

        public Task<IReadOnlyList<LaundryItem>> LockItemsAsync(MachineType type, Guid? includeItemId, CancellationToken cancellationToken) =>
            inner.LockItemsAsync(type, includeItemId, cancellationToken);

        public Task<long> NextQueueTicketAsync(CancellationToken cancellationToken) => inner.NextQueueTicketAsync(cancellationToken);

        public Task EnqueueAsync(Guid itemId, long ticket, DateTime queuedAt, CancellationToken cancellationToken) =>
            inner.EnqueueAsync(itemId, ticket, queuedAt, cancellationToken);

        public Task RequeueExpiredAsync(Guid itemId, Guid machineId, long ticket, DateTime requeuedAt, CancellationToken cancellationToken) =>
            inner.RequeueExpiredAsync(itemId, machineId, ticket, requeuedAt, cancellationToken);

        public Task OfferAsync(Guid itemId, Guid machineId, DateTime offeredAt, DateTime expiresAt, CancellationToken cancellationToken) =>
            inner.OfferAsync(itemId, machineId, offeredAt, expiresAt, cancellationToken);

        public Task<MachineType?> LocateMachineAsync(Guid machineId, CancellationToken cancellationToken) => inner.LocateMachineAsync(machineId, cancellationToken);

        public Task OccupyAsync(Guid machineId, DateTime occupiedAt, CancellationToken cancellationToken) => inner.OccupyAsync(machineId, occupiedAt, cancellationToken);

        public Task CompleteRunAsync(Guid machineId, Guid? itemId, DateTime completedAt, CancellationToken cancellationToken) =>
            inner.CompleteRunAsync(machineId, itemId, completedAt, cancellationToken);

        public Task StartAsync(Guid itemId, Guid machineId, DateTime startedAt, CancellationToken cancellationToken) =>
            inner.StartAsync(itemId, machineId, startedAt, cancellationToken);

        public Task<RefundableItem> ReadRefundableAsync(Guid itemId, CancellationToken cancellationToken) => inner.ReadRefundableAsync(itemId, cancellationToken);

        public Task<decimal> CancelWithRefundAsync(
            Guid walletId, Guid bookingId, Guid itemId, BookingItemStatus from, Guid? machineId, decimal amount, DateTime cancelledAt, CancellationToken cancellationToken) =>
            inner.CancelWithRefundAsync(walletId, bookingId, itemId, from, machineId, amount, cancelledAt, cancellationToken);

        public Task FailRunAsync(Guid machineId, Guid? itemId, DateTime failedAt, CancellationToken cancellationToken) =>
            inner.FailRunAsync(machineId, itemId, failedAt, cancellationToken);

        public Task<decimal> CreditRefundAsync(Guid walletId, Guid bookingId, Guid itemId, decimal amount, DateTime creditedAt, CancellationToken cancellationToken) =>
            inner.CreditRefundAsync(walletId, bookingId, itemId, amount, creditedAt, cancellationToken);

        public Task<Guid?> LockOpenIncidentAsync(Guid itemId, CancellationToken cancellationToken) => inner.LockOpenIncidentAsync(itemId, cancellationToken);

        public Task RecordIncidentAsync(Guid incidentId, Guid machineId, Guid itemId, string? description, DateTime reportedAt, CancellationToken cancellationToken) =>
            inner.RecordIncidentAsync(incidentId, machineId, itemId, description, reportedAt, cancellationToken);

        public async Task CommitAsync()
        {
            await beforeCommit();
            await inner.CommitAsync();
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
