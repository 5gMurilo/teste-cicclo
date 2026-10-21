using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Tests.Support;
using Cicclo.Api.Wallet;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Integration.Laundry;

/// <summary>
/// Ativação e promoção FIFO na transação de quem chama, contra PostgreSQL 18.1 real: valores esperados vêm de
/// QUEUE-04..07/14 e do design E03 (ticket novo, expiresAt = now + timeout, posição derivada, sem débito).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E03")]
[Trait("Task", "T2")]
public sealed class QueueCoordinatorTests(PostgresFixture postgres)
{
    private const string QueueApp = "cicclo-e03-queue";
    private const string HolderApp = "cicclo-e03-holder";

    [Theory]
    [InlineData(60, "12:01:00.123456")]
    [InlineData(90, "12:01:30.123456")]
    public async Task A01_FreeMachineWithoutQueueReceivesTheOfferAtOnceWithoutAnyDebit(int timeoutSeconds, string expires)
    {
        var database = await ReadyAsync(postgres);
        var item = await PendingAsync(database);
        var clock = Clock(T0.AddTicks(1_234_567));

        var result = await ActivateAsync(Sessions(database, QueueApp), Coordinator(clock, timeoutSeconds), item);

        var offeredAt = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
        Assert.Equal(ActivationOutcome.Activated, result.Outcome);
        Assert.Equal(new ItemPlacement(item, BookingItemStatus.ToBeConfirmed, Guid.Parse(WasherId), offeredAt.AddSeconds(timeoutSeconds), null), result.Item);
        Assert.Equal([new MachineOffer(item, Guid.Parse(WasherId), offeredAt, offeredAt, offeredAt.AddSeconds(timeoutSeconds))], result.Offers);
        Assert.Equal(DateTimeKind.Utc, result.Item!.ExpiresAt!.Value.Kind);
        Assert.Equal(
            [
                $"item|{item}|to_be_confirmed|1|12:00:00.123456|{WasherId}|12:00:00.123456|{expires}|null",
                $"machine|washer-01|reserved|{item}|12:00:00.123456",
                $"machine|dryer-01|available|null|{await UpdatedAtAsync(database, DryerId)}",
                "wallet|50.00|0",
            ],
            await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task A02_BusyMachineQueuesTheItemWithANewTicketAndDerivedPosition()
    {
        var database = await ReadyAsync(postgres);
        var first = await PendingAsync(database);
        var second = await PendingAsync(database);
        var clock = Clock();
        var sessions = Sessions(database, QueueApp);
        await ActivateAsync(sessions, Coordinator(clock), first);
        clock.Advance(TimeSpan.FromSeconds(5));

        var result = await ActivateAsync(sessions, Coordinator(clock), second);

        Assert.Equal(ActivationOutcome.Activated, result.Outcome);
        Assert.Equal(new ItemPlacement(second, BookingItemStatus.Queued, null, null, 1), result.Item);
        Assert.Empty(result.Offers);
        Assert.Equal(
            [$"{first}|to_be_confirmed|1|12:00:00|{WasherId}", $"{second}|queued|2|12:00:05|null"],
            await database.RowsAsync(
                "SELECT id || '|' || status || '|' || queue_ticket || '|' || to_char(queued_at AT TIME ZONE 'UTC', 'HH24:MI:SS') || '|' || coalesce(machine_id::text, 'null') FROM service_request_items ORDER BY queue_ticket"));
        Assert.Equal($"reserved|{first}", await database.ScalarAsync<string>($"SELECT state || '|' || active_item_id FROM machines WHERE id = '{WasherId}'"));
    }

    [Fact]
    public async Task A03_PendingItemsDoNotOccupyMachinesOrJoinTheQueue()
    {
        var database = await ReadyAsync(postgres);
        await InsertPendingAsync(database, (Guid.NewGuid(), "wash"), (Guid.NewGuid(), "wash"), (Guid.NewGuid(), "dry"));
        var before = await database.RowsAsync(StateSql);
        Assert.Contains(before, row => row.StartsWith("machine|washer-01|available|null", StringComparison.Ordinal));
        var latecomer = await PendingAsync(database);

        var result = await ActivateAsync(Sessions(database, QueueApp), Coordinator(Clock()), latecomer);

        Assert.Equal(BookingItemStatus.ToBeConfirmed, result.Item!.Status);
        Assert.Equal(Guid.Parse(WasherId), result.Item.MachineId);
        Assert.Equal(3L, await database.ScalarAsync<long>("SELECT count(*) FROM service_request_items WHERE status = 'pending_activation' AND queue_ticket IS NULL AND queued_at IS NULL AND machine_id IS NULL"));
        Assert.Equal("dryer-01|available|null", (await database.RowsAsync("SELECT name || '|' || state || '|' || coalesce(active_item_id::text, 'null') FROM machines WHERE name = 'dryer-01'")).Single());
    }

    [Fact]
    public async Task A04_ReleasedMachinesServeABCInTicketOrderAndRepeatedActivationKeepsTheTicket()
    {
        // Ids em ordem inversa à chegada: o ticket, não o id, decide o FIFO.
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{WasherId}'");
        var a = await PendingWithIdAsync(database, Guid.Parse("00000000-0000-4000-8000-00000000a003"));
        var b = await PendingWithIdAsync(database, Guid.Parse("00000000-0000-4000-8000-00000000a002"));
        var c = await PendingWithIdAsync(database, Guid.Parse("00000000-0000-4000-8000-00000000a001"));
        var clock = Clock();
        var sessions = Sessions(database, QueueApp);

        var placements = new List<ItemPlacement?>();
        foreach (var item in new[] { a, b, c })
        {
            placements.Add((await ActivateAsync(sessions, Coordinator(clock), item)).Item);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal([1, 2, 3], placements.Select(placement => placement!.QueuePosition));
        Assert.All(placements, placement => Assert.Equal(BookingItemStatus.Queued, placement!.Status));

        await database.ExecuteAsync($"UPDATE machines SET state = 'available', is_demo_occupied = false WHERE id = '{WasherId}'");
        var firstRelease = await PromoteAsync(sessions, Coordinator(clock), MachineType.Washer);

        Assert.Equal([(a, Guid.Parse(WasherId))], firstRelease.Select(offer => (offer.ItemId, offer.MachineId)));
        Assert.Equal(new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc), firstRelease[0].QueuedAt);
        Assert.Equal(new DateTime(2026, 10, 21, 12, 0, 3, DateTimeKind.Utc), firstRelease[0].OfferedAt);
        var repeatB = await ActivateAsync(sessions, Coordinator(clock), b);
        var repeatC = await ActivateAsync(sessions, Coordinator(clock), c);
        Assert.Equal(ActivationOutcome.AlreadyActive, repeatB.Outcome);
        Assert.Equal(new ItemPlacement(b, BookingItemStatus.Queued, null, null, 1), repeatB.Item);
        Assert.Equal(new ItemPlacement(c, BookingItemStatus.Queued, null, null, 2), repeatC.Item);
        Assert.Empty(repeatB.Offers);

        await database.ExecuteAsync($"INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES ('{Washer2Id}', 'washer-02', 'washer', 'available', now())");
        var secondRelease = await PromoteAsync(sessions, Coordinator(clock), MachineType.Washer);

        Assert.Equal([(b, Guid.Parse(Washer2Id))], secondRelease.Select(offer => (offer.ItemId, offer.MachineId)));
        Assert.Equal(
            [$"{a}|to_be_confirmed|1|{WasherId}", $"{b}|to_be_confirmed|2|{Washer2Id}", $"{c}|queued|3|null"],
            await database.RowsAsync("SELECT id || '|' || status || '|' || queue_ticket || '|' || coalesce(machine_id::text, 'null') FROM service_request_items ORDER BY queue_ticket"));
        Assert.Equal(3L, await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq"));
    }

    [Fact]
    public async Task A05_NewcomerDoesNotOvertakeAnOlderQueuedItemWhenAMachineIsFree()
    {
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{WasherId}'");
        var older = await PendingAsync(database);
        var newcomer = await PendingAsync(database);
        var clock = Clock();
        var sessions = Sessions(database, QueueApp);
        await ActivateAsync(sessions, Coordinator(clock), older);
        // A máquina fica livre sem promoção; a próxima ativação disputa a mesma máquina.
        await database.ExecuteAsync($"UPDATE machines SET state = 'available', is_demo_occupied = false WHERE id = '{WasherId}'");

        var result = await ActivateAsync(sessions, Coordinator(clock), newcomer);

        Assert.Equal(new ItemPlacement(newcomer, BookingItemStatus.Queued, null, null, 1), result.Item);
        Assert.Equal([older], result.Offers.Select(offer => offer.ItemId));
        Assert.Equal(
            [$"{older}|to_be_confirmed|1", $"{newcomer}|queued|2"],
            await database.RowsAsync("SELECT id || '|' || status || '|' || queue_ticket FROM service_request_items ORDER BY queue_ticket"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A06_TwoConcurrentActivationsReserveTheFreeMachineOnce(bool lockWallet)
    {
        var database = await ReadyAsync(postgres);
        var first = await PendingAsync(database);
        var second = await PendingAsync(database);
        var sessions = Sessions(database, QueueApp);
        var clock = Clock();

        // Um terceiro segura a lavadora: as duas ativações ficam comprovadamente paradas no lock antes de decidir.
        var (holder, transaction) = await HoldAsync(database, HolderApp, $"SELECT 1 FROM machines WHERE id = '{WasherId}' FOR UPDATE");
        var activations = new[] { first, second }.Select(item => Task.Run(() => ActivateAsync(sessions, Coordinator(clock), item, lockWallet))).ToList();
        await WaitUntilWaitingOnLockAsync(database, QueueApp, 2);
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        var results = await Task.WhenAll(activations).WaitAsync(SignalTimeout);

        Assert.Equal(
            [BookingItemStatus.Queued, BookingItemStatus.ToBeConfirmed],
            results.Select(result => result.Item!.Status).Order());
        Assert.Equal(1, results.Single(result => result.Item!.Status == BookingItemStatus.Queued).Item!.QueuePosition);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM service_request_items WHERE status = 'to_be_confirmed'"));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM machines WHERE state = 'reserved'"));
        Assert.Equal(
            results.Single(result => result.Item!.Status == BookingItemStatus.ToBeConfirmed).Item!.ItemId.ToString(),
            await database.ScalarAsync<string>($"SELECT active_item_id::text FROM machines WHERE id = '{WasherId}'"));
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT count(DISTINCT queue_ticket) FROM service_request_items"));
    }

    [Fact]
    public async Task A07_ItemWithoutOperationalMachineOfItsTypeStaysQueuedAndNeverTakesAnotherType()
    {
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'out_of_service' WHERE id = '{WasherId}'");
        var wash = await PendingAsync(database, "wash");
        var dry = await PendingAsync(database, "dry");
        var sessions = Sessions(database, QueueApp);

        var washResult = await ActivateAsync(sessions, Coordinator(Clock()), wash);
        var dryResult = await ActivateAsync(sessions, Coordinator(Clock()), dry);

        Assert.Equal(new ItemPlacement(wash, BookingItemStatus.Queued, null, null, 1), washResult.Item);
        Assert.Empty(washResult.Offers);
        Assert.Equal(new ItemPlacement(dry, BookingItemStatus.ToBeConfirmed, Guid.Parse(DryerId), T0.UtcDateTime.AddSeconds(60), null), dryResult.Item);
        Assert.Empty(await PromoteAsync(sessions, Coordinator(Clock()), MachineType.Washer));
        Assert.Equal(
            ["washer-01|out_of_service|null", $"dryer-01|reserved|{dry}"],
            await database.RowsAsync("SELECT name || '|' || state || '|' || coalesce(active_item_id::text, 'null') FROM machines ORDER BY id"));
    }

    [Fact]
    public async Task A08_FailureWhileReservingTheMachineRevertsTicketItemAndMachine()
    {
        var database = await ReadyAsync(postgres);
        var item = await PendingAsync(database);
        await database.ExecuteAsync(
            """
            CREATE FUNCTION fail_reserve() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'reserva recusada'; END $$;
            CREATE TRIGGER fail_reserve BEFORE UPDATE ON machines FOR EACH ROW WHEN (NEW.state = 'reserved') EXECUTE FUNCTION fail_reserve();
            """);
        var before = await database.RowsAsync(StateSql);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ActivateAsync(Sessions(database, QueueApp), Coordinator(Clock()), item));

        Assert.Equal("P0001", error.SqlState);
        // O ticket chegou a ser sorteado (sequências não revertem) e o item enfileirado/ofertado antes da falha na máquina:
        // nada disso ficou no item nem na máquina.
        Assert.Equal("1|True", (await database.RowsAsync("SELECT last_value, is_called FROM queue_ticket_seq")).Single());
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Contains($"item|{item}|pending_activation|null|null|null|null|null|null", before);
    }

    [Fact]
    public async Task A09_MachinesAreLockedBeforeItemsAndTheInstantIsTakenAfterTheLocks()
    {
        var database = await ReadyAsync(postgres);
        var item = await PendingAsync(database);
        var clock = Clock();
        var (holder, transaction) = await HoldAsync(database, HolderApp, $"SELECT 1 FROM machines WHERE id = '{WasherId}' FOR UPDATE");

        var activation = Task.Run(() => ActivateAsync(Sessions(database, QueueApp), Coordinator(clock), item));
        await WaitUntilWaitingOnLockAsync(database, QueueApp);

        Assert.False(await CanLockNowAsync(database, $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE"));
        Assert.True(await CanLockNowAsync(database, $"SELECT 1 FROM service_request_items WHERE id = '{item}' FOR UPDATE"));
        Assert.False(activation.IsCompleted);
        clock.SetUtcNow(T0.AddMinutes(5));
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        var result = await activation.WaitAsync(SignalTimeout);

        Assert.Equal(T0.UtcDateTime.AddMinutes(5).AddSeconds(60), result.Item!.ExpiresAt);
        Assert.Equal(
            $"item|{item}|to_be_confirmed|1|12:05:00.000000|{WasherId}|12:05:00.000000|12:06:00.000000|null",
            (await database.RowsAsync(StateSql))[0]);
    }

    [Fact]
    public async Task A10_ItemsAreLockedInTheSameTransactionUntilCommit()
    {
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{WasherId}'");
        var queued = await PendingAsync(database);
        var target = await PendingAsync(database);
        var clock = Clock();
        var sessions = Sessions(database, QueueApp);
        await ActivateAsync(sessions, Coordinator(clock), queued);

        await using var session = await sessions.OpenAsync(CancellationToken.None);
        await session.LockWalletAsync(DemoWallet.Id, CancellationToken.None);
        var result = await Coordinator(clock).ActivateAsync(session, target, CancellationToken.None);

        // Antes do commit: a fila do tipo (o item já enfileirado e o alvo) e a lavadora seguem bloqueadas pela transação.
        Assert.Equal(new ItemPlacement(target, BookingItemStatus.Queued, null, null, 2), result.Item);
        Assert.False(await CanLockNowAsync(database, $"SELECT 1 FROM service_request_items WHERE id = '{queued}' FOR UPDATE"));
        Assert.False(await CanLockNowAsync(database, $"SELECT 1 FROM service_request_items WHERE id = '{target}' FOR UPDATE"));
        Assert.False(await CanLockNowAsync(database, $"SELECT 1 FROM machines WHERE id = '{WasherId}' FOR UPDATE"));
        Assert.True(await CanLockNowAsync(database, $"SELECT 1 FROM machines WHERE id = '{DryerId}' FOR UPDATE"));
        Assert.Equal("pending_activation", await database.ScalarAsync<string>($"SELECT status FROM service_request_items WHERE id = '{target}'"));
        await session.CommitAsync();
        Assert.Equal("queued", await database.ScalarAsync<string>($"SELECT status FROM service_request_items WHERE id = '{target}'"));
    }

    [Fact]
    public async Task A11_UnknownItemIsNotFoundAndMissingDemoWalletIsAnUnpreparedDatabase()
    {
        var database = await ReadyAsync(postgres);
        var before = await database.RowsAsync(StateSql);

        var result = await ActivateAsync(Sessions(database, QueueApp), Coordinator(Clock()), Guid.NewGuid());

        Assert.Equal(ActivationOutcome.NotFound, result.Outcome);
        Assert.Null(result.Item);
        Assert.Empty(result.Offers);
        Assert.Equal(before, await database.RowsAsync(StateSql));

        await database.ExecuteAsync("DELETE FROM wallets");
        await using var session = await Sessions(database, QueueApp).OpenAsync(CancellationToken.None);
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => session.LockWalletAsync(DemoWallet.Id, CancellationToken.None));
        await Assert.ThrowsAsync<DatabaseUnavailableException>(
            () => new PostgresLaundrySessions(new UnconfiguredContextFactory()).OpenAsync(CancellationToken.None));
    }

    /// <summary>
    /// Contrato da porta (L): cada mutação confere o estado de origem e, fora dele, falha sem efeito, o que reverte a
    /// transação inteira. Sem isso, uma chamada fora de ordem daria ticket novo a item ativo ou reservaria máquina ocupada.
    /// </summary>
    [Theory]
    [InlineData("enqueue an already queued item", "out_of_service")]
    [InlineData("offer a pending item", "out_of_service")]
    [InlineData("reserve a running machine", "running")]
    [InlineData("reserve an out of service machine", "out_of_service")]
    public async Task A12_PortMutationsRefuseAnUnexpectedSourceStateWithoutEffect(string scenario, string washer2State)
    {
        var database = await ReadyAsync(postgres);
        var queued = await PendingAsync(database);
        var pending = await PendingAsync(database);
        var sessions = Sessions(database, QueueApp);
        await database.ExecuteAsync($"UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{WasherId}'");
        await ActivateAsync(sessions, Coordinator(Clock()), queued);
        await database.ExecuteAsync(
            $"""
            INSERT INTO machines (id, name, machine_type, state, updated_at, is_demo_occupied) VALUES ('{Washer2Id}', 'washer-02', 'washer', '{washer2State}', now(), {(washer2State == "running" ? "true" : "false")});
            UPDATE machines SET state = 'available', is_demo_occupied = false WHERE id = '{WasherId}';
            """);
        var before = await database.RowsAsync(StateSql);
        var offeredAt = T0.UtcDateTime;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var session = await sessions.OpenAsync(CancellationToken.None);
            await session.LockWalletAsync(DemoWallet.Id, CancellationToken.None);
            switch (scenario)
            {
                case "enqueue an already queued item":
                    await session.EnqueueAsync(queued, 99, offeredAt, CancellationToken.None);
                    break;
                case "offer a pending item":
                    await session.OfferAsync(pending, Guid.Parse(WasherId), offeredAt, offeredAt.AddSeconds(60), CancellationToken.None);
                    break;
                default:
                    await session.OfferAsync(queued, Guid.Parse(Washer2Id), offeredAt, offeredAt.AddSeconds(60), CancellationToken.None);
                    break;
            }

            await session.CommitAsync();
        });

        Assert.StartsWith("Transição de fila recusada", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task A13_ItemsAreLockedInIdOrderEvenWhenStoredInAnotherOrder()
    {
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{WasherId}'");
        var high = await PendingWithIdAsync(database, Guid.Parse("00000000-0000-4000-8000-00000000b009"));
        var low = await PendingWithIdAsync(database, Guid.Parse("00000000-0000-4000-8000-00000000b001"));
        var sessions = Sessions(database, QueueApp);
        await ActivateAsync(sessions, Coordinator(Clock()), high);
        await ActivateAsync(sessions, Coordinator(Clock()), low);
        await database.ExecuteAsync("VACUUM FULL service_request_items");
        var (holder, transaction) = await HoldAsync(database, HolderApp, $"SELECT 1 FROM service_request_items WHERE id = '{high}' FOR UPDATE");

        var promotion = Task.Run(() => PromoteAsync(sessions, Coordinator(Clock()), MachineType.Washer));
        await WaitUntilWaitingOnLockAsync(database, QueueApp);

        // A promoção já segura o item de id menor e espera o maior: a mesma ordem de qualquer outra transação.
        Assert.False(await CanLockNowAsync(database, $"SELECT 1 FROM service_request_items WHERE id = '{low}' FOR UPDATE"));
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        Assert.Empty(await promotion.WaitAsync(SignalTimeout));
    }

    [Fact]
    public async Task A14_MachinesAreLockedInIdOrderEvenWhenStoredInAnotherOrder()
    {
        // washer-00 tem id menor que washer-01, mas é gravada depois.
        const string lowerWasher = "00000000-0000-4000-8000-000000000100";
        var database = await ReadyAsync(
            postgres, $"INSERT INTO machines (id, name, machine_type, state, updated_at, is_demo_occupied) VALUES ('{lowerWasher}', 'washer-00', 'washer', 'running', now(), true)");
        var item = await PendingAsync(database);
        var (holder, transaction) = await HoldAsync(database, HolderApp, $"SELECT 1 FROM machines WHERE id = '{WasherId}' FOR UPDATE");

        var activation = Task.Run(() => ActivateAsync(Sessions(database, QueueApp), Coordinator(Clock()), item));
        await WaitUntilWaitingOnLockAsync(database, QueueApp);

        Assert.False(await CanLockNowAsync(database, $"SELECT 1 FROM machines WHERE id = '{lowerWasher}' FOR UPDATE"));
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        Assert.Equal(Guid.Parse(WasherId), (await activation.WaitAsync(SignalTimeout)).Item!.MachineId);
    }

    /// <summary>
    /// QUEUE-13 (dívida N19 da E03): repetir a ativação de um item já ofertado ou em execução devolve o estado atual, sem
    /// ticket novo, sem oferta e sem nenhuma escrita; em execução, nem o prazo vencido da oferta antiga muda algo.
    /// </summary>
    [Theory]
    [InlineData("to_be_confirmed", 10)]
    [InlineData("in_progress", 90)]
    public async Task A15_RepeatedActivationOfAnOfferedOrStartedItemReturnsTheCurrentStateWithoutANewTicket(string state, int repeatAfterSeconds)
    {
        var database = await ReadyAsync(postgres);
        var item = await PendingAsync(database);
        var clock = Clock();
        var sessions = Sessions(database, QueueApp);
        await ActivateAsync(sessions, Coordinator(clock), item);
        if (state == "in_progress")
        {
            clock.SetUtcNow(T0.AddSeconds(5));
            var booking = await database.ScalarAsync<Guid>($"SELECT request_id FROM service_request_items WHERE id = '{item}'");
            var started = await new Cicclo.Api.Laundry.ConfirmOffer.ConfirmOffer(sessions, Coordinator(clock), Metrics(), clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<Cicclo.Api.Laundry.ConfirmOffer.ConfirmOffer>.Instance)
                .ExecuteAsync(booking, item, CancellationToken.None);
            Assert.Equal(Cicclo.Api.Laundry.ConfirmOffer.ConfirmOfferOutcome.Started, started.Outcome);
        }

        clock.SetUtcNow(T0.AddSeconds(repeatAfterSeconds));
        var before = await database.RowsAsync(StateSql);
        Assert.Contains(before, row => row.StartsWith($"item|{item}|{state}|1|", StringComparison.Ordinal));

        var result = await ActivateAsync(sessions, Coordinator(clock), item);

        Assert.Equal(ActivationOutcome.AlreadyActive, result.Outcome);
        var expectedStatus = state == "in_progress" ? BookingItemStatus.InProgress : BookingItemStatus.ToBeConfirmed;
        Assert.Equal((item, expectedStatus, (Guid?)Guid.Parse(WasherId), (int?)null), (result.Item!.ItemId, result.Item.Status, result.Item.MachineId, result.Item.QueuePosition));
        if (state == "to_be_confirmed")
        {
            Assert.Equal(T0.UtcDateTime.AddSeconds(60), result.Item.ExpiresAt);
        }

        Assert.Empty(result.Offers);
        Assert.Empty(result.Expired);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq"));
    }

    private static async Task<string> UpdatedAtAsync(TestDatabase database, string machineId) =>
        await database.ScalarAsync<string>($"SELECT to_char(updated_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') FROM machines WHERE id = '{machineId}'");

    private sealed class UnconfiguredContextFactory : IDbContextFactory<CiccloDbContext>
    {
        public CiccloDbContext CreateDbContext() => new(new DbContextOptionsBuilder<CiccloDbContext>().UseNpgsql().Options);
    }
}
