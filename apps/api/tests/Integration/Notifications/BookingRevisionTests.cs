using System.Net;
using System.Text;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Laundry;
using Cicclo.Api.Tests.Integration.Laundry;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;
using ReconcileOffersUseCase = Cicclo.Api.Laundry.ReconcileOffers.ReconcileOffers;

namespace Cicclo.Api.Tests.Integration.Notifications;

/// <summary>
/// Revisão persistida do booking sobre PostgreSQL 18.1 real (RT-05/06, D09/D10): backfill 1 na migration, compra nova
/// em 1, +1 por booking distinto a cada commit que altera booking/itens (HTTP e worker), nada em leitura, replay puro,
/// rollback, posição derivada da fila, relato ou ocupação sintética. Valores conferidos no banco a cada passo.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E09")]
[Trait("Task", "T1")]
public sealed class BookingRevisionTests(PostgresFixture postgres)
{
    private const string PreviousMigration = "MachineIncidents";
    private const string WorkerApp = "cicclo-e09-worker";
    private const string HolderApp = "cicclo-e09-holder";
    private const string WashBody = """{"serviceCodes":["wash"],"expectedTotalCents":1890}""";
    private const string BookingColumnsSql =
        "SELECT id || '|' || wallet_id || '|' || idempotency_key || '|' || request_fingerprint || '|' || total_amount || '|' || to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US') FROM service_requests ORDER BY id";

    /// <summary>Simulação ligada, worker do host parado (1 h) e limites altos: só o comando do passo muda o banco.</summary>
    private static readonly Dictionary<string, string> Settings = new()
    {
        ["Laundry:DemoMode"] = "true",
        ["Laundry:QueueWorkerIntervalSeconds"] = "3600",
        ["RateLimiting:BookingCreate:PermitLimit"] = "1000",
        ["RateLimiting:BookingReads:PermitLimit"] = "1000",
        ["RateLimiting:ItemCommands:PermitLimit"] = "1000",
        ["RateLimiting:ItemCancel:PermitLimit"] = "1000",
        ["RateLimiting:IssueReports:PermitLimit"] = "1000",
    };

    [Fact]
    public async Task V01_MigrationBackfillsExistingBookingsWithRevisionOneAndPreservesTheirData()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync(PreviousMigration);
        await database.SeedAsync();
        await InsertPendingAsync(database, (Guid.NewGuid(), "wash"), (Guid.NewGuid(), "dry"));
        await InsertPendingAsync(database, (Guid.NewGuid(), "wash"));
        var bookings = await database.RowsAsync(BookingColumnsSql);
        var state = await database.RowsAsync(StateSql);

        await database.MigrateAsync();

        Assert.Equal(2, bookings.Count);
        Assert.Equal(bookings, await database.RowsAsync(BookingColumnsSql));
        Assert.Equal(state, await database.RowsAsync(StateSql));
        Assert.Equal(["1", "1"], await database.RowsAsync("SELECT revision FROM service_requests ORDER BY id"));
        Assert.Equal(TestDatabase.MigrationIds, await database.RowsAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1"));
        Assert.Equal(
            ["bigint|True|1"],
            await database.RowsAsync(
                """
                SELECT format_type(a.atttypid, a.atttypmod), a.attnotnull, pg_get_expr(d.adbin, d.adrelid)
                FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                WHERE a.attrelid = 'service_requests'::regclass AND a.attname = 'revision'
                """));
        var invalid = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("UPDATE service_requests SET revision = 0"));
        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_service_requests_revision_positive"), (invalid.SqlState, invalid.ConstraintName));
    }

    [Fact]
    public async Task V02_NewPurchaseCommitsAtRevisionOneWhileItsActivationBumpsOnlyTheOtherBookingItChanged()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings);
        using var client = factory.CreateClient();
        var first = await PurchaseAsync(client, WashBody);
        Assert.Equal(1L, await RevisionAsync(database, first.Booking));
        Assert.Equal("to_be_confirmed", await StatusAsync(database, first.Items[0]));

        // A oferta da primeira venceu: a ativação da segunda compra a devolve à fila e a reoferta no mesmo commit.
        clock.SetUtcNow(T0.AddSeconds(61));
        var second = await PurchaseAsync(client, WashBody);

        Assert.Equal(("to_be_confirmed", "queued"), (await StatusAsync(database, first.Items[0]), await StatusAsync(database, second.Items[0])));
        Assert.Equal("12:01:01.000000", await database.ScalarAsync<string>($"SELECT to_char(offered_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') FROM service_request_items WHERE id = '{first.Items[0]}'"));
        Assert.Equal([$"{first.Booking}|2", $"{second.Booking}|1"], await RevisionsAsync(database));
    }

    [Fact]
    public async Task V03_EachCommitThatChangesTheBookingIncrementsOnceAndMonotonicallyIncludingTheWorker()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings);
        using var client = factory.CreateClient();
        var booking = await PurchaseAsync(client, """{"serviceCodes":["wash","wash"],"preferredFirstServiceCode":"wash","expectedTotalCents":3780}""");
        var offered = await ItemInAsync(database, booking.Booking, "to_be_confirmed");
        var pending = await ItemInAsync(database, booking.Booking, "pending_activation");
        var revisions = new List<long> { await RevisionAsync(database, booking.Booking) };

        clock.SetUtcNow(T0.AddSeconds(5));
        await AssertStatusAsync(HttpStatusCode.OK, client.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{pending}/activate", null));
        revisions.Add(await RevisionAsync(database, booking.Booking));
        clock.SetUtcNow(T0.AddSeconds(10));
        await AssertStatusAsync(HttpStatusCode.OK, client.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{offered}/confirm", null));
        revisions.Add(await RevisionAsync(database, booking.Booking));
        // Conclusão do primeiro e oferta do segundo item do mesmo booking no mesmo commit: um único incremento.
        clock.SetUtcNow(T0.AddSeconds(20));
        await AssertStatusAsync(HttpStatusCode.OK, client.PostAsync($"/api/v1/simulation/machines/{WasherId}/complete", null));
        revisions.Add(await RevisionAsync(database, booking.Booking));
        clock.SetUtcNow(T0.AddSeconds(81));
        var reconciled = await Worker(database, clock).ExecuteAsync(CancellationToken.None);
        revisions.Add(await RevisionAsync(database, booking.Booking));
        clock.SetUtcNow(T0.AddSeconds(90));
        await AssertStatusAsync(HttpStatusCode.OK, client.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{pending}/cancel", null));
        revisions.Add(await RevisionAsync(database, booking.Booking));

        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], revisions);
        Assert.Equal([pending], reconciled.SelectMany(change => change.Expired).Select(expired => expired.ItemId));
        Assert.Equal("completed", await StatusAsync(database, offered));
        Assert.Equal("cancelled", await StatusAsync(database, pending));
        Assert.Equal(["debit|37.80|12.20", "refund|18.90|31.10"], await database.RowsAsync("SELECT kind || '|' || amount || '|' || balance_after FROM wallet_transactions ORDER BY created_at"));
    }

    [Fact]
    public async Task V04_SeveralItemsOfTheSameBookingChangedInOneWorkerCommitIncrementItOnce()
    {
        var database = await ReadyAsync(postgres);
        await database.ExecuteAsync($"INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES ('{Washer2Id}', 'washer-02', 'washer', 'available', now())");
        var clock = Clock();
        var untouched = await PendingAsync(database, "dry");
        await using var factory = BookingApi.Create(database, clock, Settings);
        using var client = factory.CreateClient();
        var booking = await PurchaseAsync(client, """{"serviceCodes":["wash","wash"],"expectedTotalCents":3780}""");
        foreach (var item in booking.Items)
        {
            await AssertStatusAsync(HttpStatusCode.OK, client.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{item}/activate", null));
        }

        Assert.Equal(3L, await RevisionAsync(database, booking.Booking));
        clock.SetUtcNow(T0.AddSeconds(61));

        var changes = await Worker(database, clock).ExecuteAsync(CancellationToken.None);

        var washer = changes.Single(change => change.Type == MachineType.Washer);
        Assert.Equal(booking.Items.Order(), washer.Expired.Select(expired => expired.ItemId).Order());
        Assert.Equal(booking.Items.Order(), washer.Offers.Select(offer => offer.ItemId).Order());
        Assert.Equal(4L, await RevisionAsync(database, booking.Booking));
        Assert.Equal(1L, await database.ScalarAsync<long>($"SELECT r.revision FROM service_requests r JOIN service_request_items i ON i.request_id = r.id WHERE i.id = '{untouched}'"));
    }

    [Fact]
    public async Task V05_ReadsAndPureReplaysLeaveTheRevisionUnchangedAndRestCarriesNoRevisionField()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        var booking = await PurchaseAsync(client, WashBody, key);
        clock.SetUtcNow(T0.AddSeconds(10));
        await AssertStatusAsync(HttpStatusCode.OK, client.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Items[0]}/confirm", null));
        Assert.Equal(2L, await RevisionAsync(database, booking.Booking));
        var state = await database.RowsAsync(StateSql);

        clock.SetUtcNow(T0.AddSeconds(20));
        using var detail = await client.GetAsync($"/api/v1/bookings/{booking.Booking}");
        using var list = await client.GetAsync("/api/v1/bookings");
        using var replay = await PostBookingAsync(client, key, WashBody);
        using var activateAgain = await client.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Items[0]}/activate", null);
        using var confirmAgain = await client.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Items[0]}/confirm", null);

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK],
            [detail.StatusCode, list.StatusCode, replay.StatusCode, activateAgain.StatusCode, confirmAgain.StatusCode]);
        Assert.Equal(2L, await RevisionAsync(database, booking.Booking));
        Assert.Equal(state, await database.RowsAsync(StateSql));
        var body = await detail.Content.ReadAsStringAsync();
        Assert.DoesNotContain("revision", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"version\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task V06_RolledBackTransactionDoesNotIncrementWhileTheSameChangeCommittedDoes()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.SetUtcNow(T0.AddSeconds(61));
        var state = await database.RowsAsync(StateSql);
        var hooked = new HookedLaundrySessions(Sessions(database, WorkerApp), () => TerminateAsync(database, WorkerApp));

        var error = await Record.ExceptionAsync(() =>
            new ReconcileOffersUseCase(hooked, Coordinator(clock), Metrics(), NullLogger<ReconcileOffersUseCase>.Instance).ExecuteAsync(CancellationToken.None));

        Assert.NotNull(error);
        Assert.Equal(state, await database.RowsAsync(StateSql));
        Assert.Equal(1L, await RevisionAsync(database, booking.BookingId));

        var committed = await Worker(database, clock).ExecuteAsync(CancellationToken.None);

        Assert.Equal([booking.Items[0].ItemId], committed.SelectMany(change => change.Expired).Select(expired => expired.ItemId));
        Assert.Equal(2L, await RevisionAsync(database, booking.BookingId));
    }

    [Fact]
    public async Task V07_DerivedQueuePositionIssueReportAndSyntheticOccupationWriteNoRevision()
    {
        // Fundos explícitos da fixture: três lavagens passam do saldo público de 50,00.
        var database = await ReadyAsync(postgres, $"UPDATE wallets SET balance = 100.00 WHERE id = '{DemoData.WalletId}'");
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings);
        using var client = factory.CreateClient();
        var running = await PurchaseAsync(client, WashBody);
        clock.SetUtcNow(T0.AddSeconds(1));
        var leaving = await PurchaseAsync(client, WashBody);
        clock.SetUtcNow(T0.AddSeconds(2));
        var waiting = await PurchaseAsync(client, WashBody);
        Assert.Equal(2, await QueuePositionAsync(client, waiting));
        clock.SetUtcNow(T0.AddSeconds(10));
        await AssertStatusAsync(HttpStatusCode.OK, client.PostAsync($"/api/v1/bookings/{running.Booking}/items/{running.Items[0]}/confirm", null));
        await AssertStatusAsync(HttpStatusCode.OK, client.PostAsync($"/api/v1/bookings/{leaving.Booking}/items/{leaving.Items[0]}/cancel", null));
        var before = await RevisionsAsync(database);

        clock.SetUtcNow(T0.AddSeconds(20));
        await AssertStatusAsync(
            HttpStatusCode.Accepted,
            client.PostAsync($"/api/v1/bookings/{running.Booking}/items/{running.Items[0]}/report-issue", new StringContent("""{"description":"barulho"}""", Encoding.UTF8, "application/json")));
        await AssertStatusAsync(HttpStatusCode.OK, client.PostAsync($"/api/v1/simulation/machines/{DryerId}/occupy", null));

        Assert.Equal(1, await QueuePositionAsync(client, waiting));
        Assert.Equal([$"{running.Booking}|2", $"{leaving.Booking}|2", $"{waiting.Booking}|1"], before);
        Assert.Equal(before, await RevisionsAsync(database));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM machine_incidents WHERE status = 'reported'"));
        Assert.Equal("running|true", await database.ScalarAsync<string>($"SELECT state || '|' || is_demo_occupied FROM machines WHERE id = '{DryerId}'"));
    }

    [Fact]
    public async Task V08_RevisionBecomesVisibleOnlyTogetherWithTheItemChangeItVersions()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var itemSql = $"SELECT i.status || '|' || to_char(i.offered_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') || '|' || r.revision FROM service_request_items i JOIN service_requests r ON r.id = i.request_id WHERE i.id = '{booking.Items[0].ItemId}'";
        clock.SetUtcNow(T0.AddSeconds(61));
        var (holder, hold) = await HoldAsync(database, HolderApp, $"SELECT 1 FROM service_requests WHERE id = '{booking.BookingId}' FOR NO KEY UPDATE");
        Task<IReadOnlyList<QueueChanges>> worker;
        await using (holder)
        await using (hold)
        {
            // O worker já devolveu e reofertou o item e espera a linha do booking para a revisão: nada disso é visível ainda.
            worker = Worker(database, clock).ExecuteAsync(CancellationToken.None);
            await WaitUntilWaitingOnLockAsync(database, WorkerApp);
            Assert.Equal("to_be_confirmed|12:00:00.000000|1", await database.ScalarAsync<string>(itemSql));
            await hold.CommitAsync();
        }

        Assert.Equal([booking.Items[0].ItemId], (await worker).SelectMany(change => change.Expired).Select(expired => expired.ItemId));
        Assert.Equal("to_be_confirmed|12:01:01.000000|2", await database.ScalarAsync<string>(itemSql));
    }

    private static ReconcileOffersUseCase Worker(TestDatabase database, FakeTimeProvider clock) =>
        new(Sessions(database, WorkerApp), Coordinator(clock), Metrics(), NullLogger<ReconcileOffersUseCase>.Instance);

    private static async Task<(Guid Booking, Guid[] Items)> PurchaseAsync(HttpClient client, string body, Guid? key = null)
    {
        using var response = await PostBookingAsync(client, key ?? Guid.NewGuid(), body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await BookingApi.JsonAsync(response);
        return (json.GetProperty("bookingId").GetGuid(), [.. json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("itemId").GetGuid())]);
    }

    private static Task<HttpResponseMessage> PostBookingAsync(HttpClient client, Guid key, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(CreateBookingEndpoint.IdempotencyKeyHeader, key.ToString());
        return client.SendAsync(request);
    }

    private static async Task<int> QueuePositionAsync(HttpClient client, (Guid Booking, Guid[] Items) booking)
    {
        var detail = await BookingApi.JsonAsync(await client.GetAsync($"/api/v1/bookings/{booking.Booking}"));
        var item = detail.GetProperty("items").EnumerateArray().Single(candidate => candidate.GetProperty("itemId").GetGuid() == booking.Items[0]);
        return item.GetProperty("queuePosition").GetInt32();
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call;
        Assert.Equal(expected, response.StatusCode);
    }

    private static Task<long> RevisionAsync(TestDatabase database, Guid booking) =>
        database.ScalarAsync<long>($"SELECT revision FROM service_requests WHERE id = '{booking}'");

    private static Task<IReadOnlyList<string>> RevisionsAsync(TestDatabase database) =>
        database.RowsAsync("SELECT id || '|' || revision FROM service_requests ORDER BY created_at, id");

    private static Task<string> StatusAsync(TestDatabase database, Guid item) =>
        database.ScalarAsync<string>($"SELECT status FROM service_request_items WHERE id = '{item}'");

    private static Task<Guid> ItemInAsync(TestDatabase database, Guid booking, string status) =>
        database.ScalarAsync<Guid>($"SELECT id FROM service_request_items WHERE request_id = '{booking}' AND status = '{status}'");

    private static async Task TerminateAsync(TestDatabase database, string applicationName) =>
        Assert.Equal(["true"], await database.RowsAsync(
            $"SELECT pg_terminate_backend(pid, {(int)SignalTimeout.TotalMilliseconds})::text FROM pg_stat_activity WHERE datname = current_database() AND application_name = '{applicationName}'"));
}
