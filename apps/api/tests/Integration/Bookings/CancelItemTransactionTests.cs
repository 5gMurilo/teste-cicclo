using System.Net;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static Cicclo.Api.Tests.Contracts.Bookings.CancelItemTests;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;
using ReconcileOffersUseCase = Cicclo.Api.Laundry.ReconcileOffers.ReconcileOffers;

namespace Cicclo.Api.Tests.Integration.Bookings;

/// <summary>
/// Cancelamento como unidade transacional em PostgreSQL 18.1 real (REFUND-02/05, FIN-08/11 regressão): corridas com
/// interleaving fixado pelo lock da wallet (sinal de pg_stat_activity), um único refund garantido também pelo índice e
/// falhas de ledger, liberação ou commit que revertem item, máquina, saldo e lançamento juntos.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E05")]
[Trait("Task", "T1")]
public sealed class CancelItemTransactionTests(PostgresFixture postgres)
{
    private const string ApiApp = "cicclo-e05-api";
    private const string ReconcileApp = "cicclo-e05-reconcile";
    private const string HolderApp = "cicclo-e05-holder";
    private static readonly string WalletLock = $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE";
    private static readonly Dictionary<string, string> Unlimited = new()
    {
        ["RateLimiting:ItemCancel:PermitLimit"] = "1000",
        ["RateLimiting:ItemCommands:PermitLimit"] = "1000",
    };

    [Fact]
    public async Task T01_TwoConcurrentCancellationsProduceExactlyOneRefundAndOneCredit()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Unlimited);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, HolderApp, WalletLock);

        var first = CancelAsync(client, booking.BookingId, wash);
        var second = CancelAsync(client, booking.BookingId, wash);
        await WaitUntilWaitingOnLockAsync(database, ApiApp, count: 2);
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var a = await first.WaitAsync(SignalTimeout);
        using var b = await second.WaitAsync(SignalTimeout);

        foreach (var response in new[] { a, b })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await BookingApi.JsonAsync(response);
            Assert.Equal((1890L, 5000L), (body.GetProperty("refundedAmountCents").GetInt64(), body.GetProperty("remainingBalanceCents").GetInt64()));
        }

        Assert.Equal(["debit|null|18.90|31.10", $"refund|{wash}|18.90|50.00"], await database.RowsAsync(LedgerSql));
        Assert.Contains("wallet|50.00|2", await database.RowsAsync(StateSql));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T02_CancelAndConfirmOnTheSameOfferHaveASingleValidTransition(bool cancelFirst)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(30));
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Unlimited);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, HolderApp, WalletLock);
        Task<HttpResponseMessage> Confirm() => client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/{wash}/confirm", null);

        var firstTask = cancelFirst ? CancelAsync(client, booking.BookingId, wash) : Confirm();
        await WaitUntilWaitingOnLockAsync(database, ApiApp);
        var secondTask = cancelFirst ? Confirm() : CancelAsync(client, booking.BookingId, wash);
        await WaitUntilWaitingOnLockAsync(database, ApiApp, count: 2);
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var firstResponse = await firstTask.WaitAsync(SignalTimeout);
        using var secondResponse = await secondTask.WaitAsync(SignalTimeout);
        var (cancel, confirm) = cancelFirst ? (firstResponse, secondResponse) : (secondResponse, firstResponse);

        var rows = await database.RowsAsync(StateSql);
        if (cancelFirst)
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            await ProblemAssert.HasProblemAsync(
                confirm, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, $"/api/v1/bookings/{booking.BookingId}/items/{wash}/confirm");
            Assert.Contains($"item|{wash}|cancelled|1|12:00:00.000000|null|null|null|null", rows);
            Assert.Contains("machine|washer-01|available|null|12:00:30.000000", rows);
            Assert.Equal(["debit|null|18.90|31.10", $"refund|{wash}|18.90|50.00"], await database.RowsAsync(LedgerSql));
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            await ProblemAssert.HasProblemAsync(cancel, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(booking.BookingId, wash));
            Assert.Contains($"item|{wash}|in_progress|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:30.000000", rows);
            Assert.Contains($"machine|washer-01|running|{wash}|12:00:30.000000", rows);
            Assert.Equal(["debit|null|18.90|31.10"], await database.RowsAsync(LedgerSql));
        }
    }

    // Oferta de A vencida aos 60 s e B na fila: worker e cancelamento de A disputam o lock da wallet. Em qualquer ordem
    // só há uma transição para A, B recebe a máquina uma vez e A cancelado não volta à fila com ticket novo.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T03_CancelAndOfferExpiryOrPromotionKeepTheQueueConsistent(bool cancelFirst)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var first = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.SetUtcNow(T0.AddSeconds(1));
        var second = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var a = first.Items[0].ItemId;
        var b = second.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(60));
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Unlimited);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, HolderApp, WalletLock);
        Task<IReadOnlyList<Cicclo.Api.Laundry.QueueChanges>> Reconcile() =>
            Task.Run(() => new ReconcileOffersUseCase(Sessions(database, ReconcileApp), Coordinator(clock), Metrics(), NullLogger<ReconcileOffersUseCase>.Instance)
                .ExecuteAsync(CancellationToken.None));

        Task<HttpResponseMessage> cancel;
        Task<IReadOnlyList<Cicclo.Api.Laundry.QueueChanges>> worker;
        if (cancelFirst)
        {
            cancel = CancelAsync(client, first.BookingId, a);
            await WaitUntilWaitingOnLockAsync(database, ApiApp);
            worker = Reconcile();
            await WaitUntilWaitingOnLockAsync(database, ReconcileApp);
        }
        else
        {
            worker = Reconcile();
            await WaitUntilWaitingOnLockAsync(database, ReconcileApp);
            cancel = CancelAsync(client, first.BookingId, a);
            await WaitUntilWaitingOnLockAsync(database, ApiApp);
        }

        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var response = await cancel.WaitAsync(SignalTimeout);
        var reconciled = await worker.WaitAsync(SignalTimeout);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1890L, (await BookingApi.JsonAsync(response)).GetProperty("refundedAmountCents").GetInt64());
        var rows = await database.RowsAsync(StateSql);
        Assert.Contains($"item|{b}|to_be_confirmed|2|12:00:01.000000|{WasherId}|12:01:00.000000|12:02:00.000000|null", rows);
        Assert.Contains($"machine|washer-01|reserved|{b}|12:01:00.000000", rows);
        Assert.Equal(["debit|null|18.90|31.10", "debit|null|18.90|12.20", $"refund|{a}|18.90|31.10"], await database.RowsAsync(LedgerSql));
        if (cancelFirst)
        {
            // O cancelamento já liberou e promoveu: o worker não encontra oferta vencida e A não ganha ticket novo.
            Assert.Equal((0, 0), (reconciled.Sum(change => change.Expired.Count), reconciled.Sum(change => change.Offers.Count)));
            Assert.Contains($"item|{a}|cancelled|1|12:00:00.000000|null|null|null|null", rows);
            Assert.Equal(2L, await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq"));
        }
        else
        {
            // O worker devolveu A ao fim da fila (ticket 3) e ofertou a B; o cancelamento retira A da fila sem mexer em B.
            Assert.Equal((1, 1), (reconciled.Sum(change => change.Expired.Count), reconciled.Sum(change => change.Offers.Count)));
            Assert.Contains($"item|{a}|cancelled|3|12:01:00.000000|null|null|null|null", rows);
            Assert.Equal(3L, await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq"));
        }
    }

    [Theory]
    [InlineData("CREATE TRIGGER fail_cancel BEFORE INSERT ON wallet_transactions FOR EACH ROW EXECUTE FUNCTION fail_cancel();")]
    [InlineData("CREATE TRIGGER fail_cancel BEFORE UPDATE ON machines FOR EACH ROW WHEN (NEW.state = 'available') EXECUTE FUNCTION fail_cancel();")]
    [InlineData("CREATE CONSTRAINT TRIGGER fail_cancel AFTER INSERT ON wallet_transactions DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_cancel();")]
    public async Task T04_FailureInTheLedgerTheMachineReleaseOrTheCommitRevertsEverything(string trigger)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var first = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.SetUtcNow(T0.AddSeconds(1));
        await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var a = first.Items[0].ItemId;
        await database.ExecuteAsync(
            $"""
            CREATE FUNCTION fail_cancel() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada SELECT secreto'; END $$;
            {trigger}
            """);
        clock.SetUtcNow(T0.AddSeconds(20));
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        var before = await database.RowsAsync(StateSql);
        var ledger = await database.RowsAsync(LedgerSql);

        using var response = await CancelAsync(factory.CreateClient(), first.BookingId, a);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.InternalServerError, ApiProblems.InternalError, Path(first.BookingId, a));
        Assert.DoesNotContain("secreto", problem.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Equal(ledger, await database.RowsAsync(LedgerSql));
        Assert.Contains($"item|{a}|to_be_confirmed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|null", before);
    }

    [Fact]
    public async Task T05_TheDatabaseKeepsOneRefundPerItemAndRequiresTheItemOnRefunds()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        await using (var factory = BookingApi.Create(database, clock, Unlimited))
        {
            using var cancelled = await CancelAsync(factory.CreateClient(), booking.BookingId, wash);
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        }

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            $"INSERT INTO wallet_transactions (id, wallet_id, request_id, request_item_id, kind, amount, balance_after, created_at) VALUES (gen_random_uuid(), '{DemoData.WalletId}', '{booking.BookingId}', '{wash}', 'refund', 18.90, 68.90, now())"));
        var withoutItem = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            $"INSERT INTO wallet_transactions (id, wallet_id, request_id, request_item_id, kind, amount, balance_after, created_at) VALUES (gen_random_uuid(), '{DemoData.WalletId}', '{booking.BookingId}', NULL, 'refund', 18.90, 68.90, now())"));

        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_wallet_transactions_item_refund"), (duplicate.SqlState, duplicate.ConstraintName));
        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_wallet_transactions_refund_with_item"), (withoutItem.SqlState, withoutItem.ConstraintName));
        Assert.Single(await database.RowsAsync(LedgerSql), row => row.StartsWith("refund|", StringComparison.Ordinal));
    }
}
