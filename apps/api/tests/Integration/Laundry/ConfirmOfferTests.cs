using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry;
using Cicclo.Api.Laundry.ConfirmOffer;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;
using ConfirmOfferUseCase = Cicclo.Api.Laundry.ConfirmOffer.ConfirmOffer;

namespace Cicclo.Api.Tests.Integration.Laundry;

/// <summary>
/// Confirmação da oferta corrente contra PostgreSQL 18.1 real, com FakeTimeProvider (nenhuma espera real de 60 s):
/// QUEUE-08 (inicia sem débito), QUEUE-09 (now &gt;= expiresAt recusa sem worker), QUEUE-13 (repetição sem efeito),
/// QUEUE-14 (vínculo/estado inválidos recusam) e D02 (instante capturado depois dos locks).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E03")]
[Trait("Task", "T3")]
public sealed class ConfirmOfferTests(PostgresFixture postgres)
{
    private const string ConfirmApp = "cicclo-e03-confirm";
    private const string HolderApp = "cicclo-e03-holder";

    [Theory]
    [InlineData(59_000_000L, "12:00:59.000000")]
    [InlineData(59_999_999L, "12:00:59.999999")]
    public async Task C01_ConfirmingBeforeTheDeadlineStartsItemAndMachineWithoutDebit(long elapsedMicroseconds, string startedAt)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (bookingId, item) = await OfferedAsync(database, clock);
        clock.Advance(TimeSpan.FromMicroseconds(elapsedMicroseconds));

        var result = await UseCase(database, clock).ExecuteAsync(bookingId, item, CancellationToken.None);

        Assert.Equal(ConfirmOfferOutcome.Started, result.Outcome);
        Assert.Equal(new ItemPlacement(item, BookingItemStatus.InProgress, Guid.Parse(WasherId), T0.UtcDateTime.AddSeconds(60), null), result.Item);
        Assert.Equal(
            [
                $"item|{item}|in_progress|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|{startedAt}",
                $"machine|washer-01|running|{item}|{startedAt}",
            ],
            (await database.RowsAsync(StateSql)).Take(2));
        Assert.Equal("wallet|50.00|0", (await database.RowsAsync(StateSql))[^1]);
    }

    [Theory]
    [InlineData(60_000_000L)]
    [InlineData(61_000_000L)]
    [InlineData(600_000_000L)]
    public async Task C02_AtOrAfterTheDeadlineTheOfferIsExpiredEvenWithoutAWorker(long elapsedMicroseconds)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (bookingId, item) = await OfferedAsync(database, clock);
        clock.Advance(TimeSpan.FromMicroseconds(elapsedMicroseconds));

        var result = await UseCase(database, clock).ExecuteAsync(bookingId, item, CancellationToken.None);

        Assert.Equal(ConfirmOfferOutcome.Expired, result.Outcome);
        Assert.NotEqual(BookingItemStatus.InProgress, result.Item!.Status);
        Assert.Equal(
            "False|0|0|50.00",
            (await database.RowsAsync(
                $"SELECT (SELECT count(*) > 0 FROM service_request_items WHERE status = 'in_progress' OR started_at IS NOT NULL), (SELECT count(*) FROM machines WHERE state = 'running'), (SELECT count(*) FROM wallet_transactions), (SELECT balance FROM wallets)")).Single());
    }

    [Theory]
    [InlineData("crossed links")]
    [InlineData("machine running for the item")]
    [InlineData("queued item")]
    [InlineData("pending item")]
    public async Task C03_StateOrMachineLinkOutsideTheRuleIsRefusedWithoutMutation(string scenario)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (bookingId, item) = await OfferedAsync(database, clock);
        var target = item;
        switch (scenario)
        {
            case "crossed links":
                // Duas ofertas com vínculos cruzados: cada item aponta uma lavadora que está reservada para o outro.
                var other = await PendingAsync(database);
                await database.ExecuteAsync(
                    $"""
                    INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES ('{Washer2Id}', 'washer-02', 'washer', 'available', now());
                    UPDATE service_request_items SET status = 'to_be_confirmed', queue_ticket = 50, queued_at = '2026-10-21 12:00:00+00', machine_id = '{Washer2Id}', offered_at = '2026-10-21 12:00:00+00', expires_at = '2026-10-21 12:01:00+00' WHERE id = '{other}';
                    UPDATE machines SET state = 'reserved', active_item_id = '{other}' WHERE id = '{WasherId}';
                    UPDATE machines SET state = 'reserved', active_item_id = '{item}' WHERE id = '{Washer2Id}';
                    """);
                break;
            case "machine running for the item":
                await database.ExecuteAsync($"UPDATE machines SET state = 'running' WHERE id = '{WasherId}'");
                break;
            case "queued item":
                await database.ExecuteAsync($"UPDATE machines SET state = 'running', active_item_id = NULL WHERE id = '{WasherId}'");
                target = await PendingAsync(database);
                await ActivateAsync(Sessions(database, ConfirmApp), Coordinator(clock), target);
                break;
            default:
                target = await PendingAsync(database);
                break;
        }

        var targetBooking = scenario is "queued item" or "pending item" ? await BookingOfAsync(database, target) : bookingId;
        var before = await database.RowsAsync(StateSql);
        clock.Advance(TimeSpan.FromSeconds(10));

        var result = await UseCase(database, clock).ExecuteAsync(targetBooking, target, CancellationToken.None);

        Assert.Equal(ConfirmOfferOutcome.InvalidState, result.Outcome);
        Assert.Equal(target, result.Item!.ItemId);
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task C04_ItemOfAnotherBookingOrUnknownIsNotFoundWithoutMutation()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (_, item) = await OfferedAsync(database, clock);
        var otherBooking = await BookingOfAsync(database, await PendingAsync(database, "dry"));
        var before = await database.RowsAsync(StateSql);
        clock.Advance(TimeSpan.FromSeconds(10));

        var wrongBooking = await UseCase(database, clock).ExecuteAsync(otherBooking, item, CancellationToken.None);
        var unknown = await UseCase(database, clock).ExecuteAsync(otherBooking, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(new ConfirmOfferResult(ConfirmOfferOutcome.NotFound), wrongBooking);
        Assert.Equal(new ConfirmOfferResult(ConfirmOfferOutcome.NotFound), unknown);
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task C05_RepeatingAConfirmedOfferNeitherRestartsNorDebits()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (bookingId, item) = await OfferedAsync(database, clock);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(ConfirmOfferOutcome.Started, (await UseCase(database, clock).ExecuteAsync(bookingId, item, CancellationToken.None)).Outcome);
        var started = await database.RowsAsync(StateSql);

        // Depois do prazo da oferta: a repetição ainda é sucesso sem efeito, não expiração.
        clock.Advance(TimeSpan.FromSeconds(90));
        var repeat = await UseCase(database, clock).ExecuteAsync(bookingId, item, CancellationToken.None);

        Assert.Equal(ConfirmOfferOutcome.AlreadyStarted, repeat.Outcome);
        Assert.Equal(new ItemPlacement(item, BookingItemStatus.InProgress, Guid.Parse(WasherId), T0.UtcDateTime.AddSeconds(60), null), repeat.Item);
        Assert.Equal(started, await database.RowsAsync(StateSql));
        Assert.Contains($"item|{item}|in_progress|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:30.000000", started);
        Assert.Equal("wallet|50.00|0", started[^1]);
    }

    [Theory]
    [InlineData("wallet")]
    [InlineData("machine")]
    [InlineData("item")]
    public async Task C06_ARequestThatWaitedForALockDecidesWithTheInstantAfterTheLock(string held)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (bookingId, item) = await OfferedAsync(database, clock);
        clock.Advance(TimeSpan.FromSeconds(59));
        var lockSql = held switch
        {
            "wallet" => $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE",
            "machine" => $"SELECT 1 FROM machines WHERE id = '{WasherId}' FOR UPDATE",
            _ => $"SELECT 1 FROM service_request_items WHERE id = '{item}' FOR UPDATE",
        };
        var (holder, transaction) = await HoldAsync(database, HolderApp, lockSql);

        var confirm = Task.Run(() => UseCase(database, clock).ExecuteAsync(bookingId, item, CancellationToken.None));
        await WaitUntilWaitingOnLockAsync(database, ConfirmApp);
        Assert.False(confirm.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        var result = await confirm.WaitAsync(SignalTimeout);

        Assert.Equal(ConfirmOfferOutcome.Expired, result.Outcome);
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM service_request_items WHERE started_at IS NOT NULL"));
    }

    [Fact]
    public async Task C07_ConfirmingOneOfferLeavesOtherOffersAndBookingsUntouched()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (washBooking, wash) = await OfferedAsync(database, clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        var (dryBooking, dry) = await OfferedAsync(database, clock, "dry");
        clock.Advance(TimeSpan.FromSeconds(5));

        var first = await UseCase(database, clock).ExecuteAsync(washBooking, wash, CancellationToken.None);

        Assert.Equal(ConfirmOfferOutcome.Started, first.Outcome);
        Assert.NotEqual(washBooking, dryBooking);
        string[] expected =
        [
            $"item|{wash}|in_progress|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:10.000000",
            $"item|{dry}|to_be_confirmed|2|12:00:05.000000|{DryerId}|12:00:05.000000|12:01:05.000000|null",
            $"machine|washer-01|running|{wash}|12:00:10.000000",
            $"machine|dryer-01|reserved|{dry}|12:00:05.000000",
            "wallet|50.00|0",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), (await database.RowsAsync(StateSql)).Order(StringComparer.Ordinal));

        clock.Advance(TimeSpan.FromSeconds(54));
        Assert.Equal(ConfirmOfferOutcome.Started, (await UseCase(database, clock).ExecuteAsync(dryBooking, dry, CancellationToken.None)).Outcome);
        Assert.Equal("12:01:05.000000", await database.ScalarAsync<string>($"SELECT to_char(expires_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') FROM service_request_items WHERE id = '{dry}'"));
        Assert.Equal("12:01:04.000000", await database.ScalarAsync<string>($"SELECT to_char(started_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') FROM service_request_items WHERE id = '{dry}'"));
    }

    [Fact]
    public async Task C08_LocksAreTakenWalletThenMachinesThenItems()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (bookingId, item) = await OfferedAsync(database, clock);
        var (holder, transaction) = await HoldAsync(database, HolderApp, $"SELECT 1 FROM machines WHERE id = '{WasherId}' FOR UPDATE");

        var confirm = Task.Run(() => UseCase(database, clock).ExecuteAsync(bookingId, item, CancellationToken.None));
        await WaitUntilWaitingOnLockAsync(database, ConfirmApp);

        Assert.False(await CanLockNowAsync(database, $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE"));
        Assert.True(await CanLockNowAsync(database, $"SELECT 1 FROM service_request_items WHERE id = '{item}' FOR UPDATE"));
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        Assert.Equal(ConfirmOfferOutcome.Started, (await confirm.WaitAsync(SignalTimeout)).Outcome);
    }

    /// <summary>Contrato da porta (L): iniciar confere item e máquina de origem; fora disso falha e nada fica gravado.</summary>
    [Theory]
    [InlineData("queued item")]
    [InlineData("other machine id")]
    [InlineData("machine not reserved for the item")]
    public async Task C09_StartRefusesAnUnexpectedSourceStateWithoutEffect(string scenario)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (_, item) = await OfferedAsync(database, clock);
        var target = item;
        var machine = Guid.Parse(WasherId);
        if (scenario == "queued item")
        {
            target = await PendingAsync(database);
            await ActivateAsync(Sessions(database, ConfirmApp), Coordinator(clock), target);
        }
        else if (scenario == "other machine id")
        {
            machine = Guid.Parse(DryerId);
        }
        else
        {
            await database.ExecuteAsync($"UPDATE machines SET state = 'running', active_item_id = NULL WHERE id = '{WasherId}'");
        }

        var before = await database.RowsAsync(StateSql);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var session = await Sessions(database, ConfirmApp).OpenAsync(CancellationToken.None);
            await session.LockWalletAsync(Cicclo.Api.Wallet.DemoWallet.Id, CancellationToken.None);
            await session.StartAsync(target, machine, T0.UtcDateTime.AddSeconds(10), CancellationToken.None);
            await session.CommitAsync();
        });

        Assert.StartsWith("Transição de fila recusada", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    private static ConfirmOfferUseCase UseCase(TestDatabase database, TimeProvider clock) =>
        new(Sessions(database, ConfirmApp), Coordinator(clock), Metrics(), clock, NullLogger<ConfirmOfferUseCase>.Instance);

    /// <summary>Item pendente ativado numa máquina livre no instante atual do relógio: oferta corrente de 60 s.</summary>
    private static async Task<(Guid BookingId, Guid ItemId)> OfferedAsync(TestDatabase database, FakeTimeProvider clock, string code = "wash")
    {
        var item = await PendingAsync(database, code);
        var activation = await ActivateAsync(Sessions(database, "cicclo-e03-setup"), Coordinator(clock), item);
        Assert.Equal(BookingItemStatus.ToBeConfirmed, activation.Item!.Status);
        return (await BookingOfAsync(database, item), item);
    }

    private static async Task<Guid> BookingOfAsync(TestDatabase database, Guid item) =>
        await database.ScalarAsync<Guid>($"SELECT request_id FROM service_request_items WHERE id = '{item}'");
}
