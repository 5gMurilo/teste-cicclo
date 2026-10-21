using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Tests.Integration.Operation;
using Cicclo.Api.Tests.Support;
using Npgsql;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Integration.Acceptance;

/// <summary>
/// Roteiro do README reproduzido de ponta a ponta: cada cenário prepara um banco `cicclo` novo com o artefato publicado
/// em `--bootstrap-demo` (o comando do guia), usa a API real por HTTP sobre PostgreSQL 18.1 com relógio controlado e
/// confere HTTP e banco juntos. O container é exclusivo desta classe; nenhum cenário toca banco, volume ou Compose pessoal.
/// Os cenários integram capacidades já testadas nas suas próprias classes; não as substituem.
/// </summary>
[Trait("Increment", "E07")]
[Trait("Task", "T2")]
public sealed class DemonstrationAcceptanceTests(ResetDemoDatabaseFixture postgres, PublishedApiFixture api)
    : IClassFixture<ResetDemoDatabaseFixture>, IClassFixture<PublishedApiFixture>
{
    private const string WashAndDry = """{"serviceCodes":["wash","dry"],"preferredFirstServiceCode":"wash","expectedTotalCents":3980}""";
    private const string Wash = """{"serviceCodes":["wash"],"expectedTotalCents":1890}""";

    private const string LedgerSql =
        "SELECT kind || '|' || coalesce(request_item_id::text, 'null') || '|' || amount || '|' || balance_after FROM wallet_transactions ORDER BY created_at, kind";

    /// <summary>Simulação ligada como no roteiro; worker com intervalo de 1 h para que toda mudança observada venha do comando.</summary>
    private static readonly Dictionary<string, string> Demo = new()
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
    public async Task A01_CleanBaseFromTheGuideChargesWashAndDry3980AndLeaves1020()
    {
        var database = await NewDemoAsync();
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();

        Assert.Equal("Healthy", await client.GetStringAsync("/health/ready"));
        Assert.Equal(5000L, await BalanceAsync(client));
        using (var services = JsonDocument.Parse(await client.GetStringAsync("/api/v1/services")))
        {
            Assert.Equal(
                ["dry|dryer|2090|True|null", "wash|washer|1890|True|null"],
                services.RootElement.GetProperty("services").EnumerateArray().Select(service => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{service.GetProperty("code").GetString()}|{service.GetProperty("machineType").GetString()}|{service.GetProperty("unitPriceCents").GetInt64()}|{service.GetProperty("canBeBooked").GetBoolean()}|{service.GetProperty("unavailabilityReason").GetString() ?? "null"}")));
        }

        Assert.Equal(["dryer-01|available|null|False", "washer-01|available|null|False"], await MachinesAsync(client));

        using var created = await BuyAsync(client, Guid.NewGuid(), WashAndDry);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var booking = await BookingApi.JsonAsync(created);
        var bookingId = booking.GetProperty("bookingId").GetGuid();
        Assert.Equal($"/api/v1/bookings/{bookingId}", created.Headers.Location?.OriginalString);
        Assert.Equal((3980L, 1020L), (booking.GetProperty("chargedAmountCents").GetInt64(), booking.GetProperty("remainingBalanceCents").GetInt64()));
        Assert.Equal(["dry|2090|pending_activation|null|null|null", "wash|1890|to_be_confirmed|washer|null|12:01:00"], Items(booking));
        Assert.Equal(1020L, await BalanceAsync(client));
        using (var ledger = JsonDocument.Parse(await client.GetStringAsync("/api/v1/wallet/transactions")))
        {
            var entry = Assert.Single(ledger.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(
                (bookingId, JsonValueKind.Null, "debit", 3980L, 1020L),
                (entry.GetProperty("bookingId").GetGuid(), entry.GetProperty("itemId").ValueKind, entry.GetProperty("kind").GetString(), entry.GetProperty("amountCents").GetInt64(), entry.GetProperty("balanceAfterCents").GetInt64()));
        }

        Assert.Equal(["debit|null|39.80|10.20"], await database.RowsAsync(LedgerSql));
        Assert.Equal(["10.20|1|2"], await database.RowsAsync("SELECT (SELECT balance FROM wallets) || '|' || (SELECT count(*) FROM service_requests) || '|' || (SELECT count(*) FROM service_request_items)"));

        // Repetir o bootstrap do guia não restaura o saldo nem desfaz a compra.
        var again = await api.BootstrapAsync(database.ConnectionString);

        Assert.True(again.ExitCode == 0, again.Output);
        Assert.Equal(1020L, await BalanceAsync(client));
        Assert.Equal(["debit|null|39.80|10.20"], await database.RowsAsync(LedgerSql));
    }

    [Fact]
    public async Task A02_ReplayWithTheSameKeyAnswers200WithoutASecondDebit()
    {
        var database = await NewDemoAsync();
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var first = await BuyAsync(client, key, WashAndDry);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var bookingId = (await BookingApi.JsonAsync(first)).GetProperty("bookingId").GetGuid();
        clock.Advance(TimeSpan.FromSeconds(5));

        using var reordered = await BuyAsync(client, key, """{"expectedTotalCents":3980,"serviceCodes":["dry","wash"],"preferredFirstServiceCode":"wash"}""");
        await database.ExecuteAsync("UPDATE services SET current_price = 25.00 WHERE code = 'wash'");
        using var afterPriceChange = await BuyAsync(client, key, WashAndDry);
        using var otherIntent = await BuyAsync(client, key, """{"serviceCodes":["wash"],"expectedTotalCents":2500}""");

        foreach (var replay in new[] { reordered, afterPriceChange })
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var body = await BookingApi.JsonAsync(replay);
            Assert.Equal(
                (bookingId, 3980L, 1020L),
                (body.GetProperty("bookingId").GetGuid(), body.GetProperty("chargedAmountCents").GetInt64(), body.GetProperty("remainingBalanceCents").GetInt64()));
            Assert.Equal(["dry|2090|pending_activation|null|null|null", "wash|1890|to_be_confirmed|washer|null|12:01:00"], Items(body));
        }

        await ProblemAssert.HasProblemAsync(otherIntent, HttpStatusCode.Conflict, "IDEMPOTENCY_CONFLICT", "/api/v1/bookings");
        Assert.Equal(["debit|null|39.80|10.20"], await database.RowsAsync(LedgerSql));
        Assert.Equal(["10.20|1|2"], await database.RowsAsync("SELECT (SELECT balance FROM wallets) || '|' || (SELECT count(*) FROM service_requests) || '|' || (SELECT count(*) FROM service_request_items)"));
        Assert.Equal(1020L, await BalanceAsync(client));
        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/bookings"));
        Assert.Equal(bookingId, Assert.Single(list.RootElement.GetProperty("items").EnumerateArray()).GetProperty("bookingId").GetGuid());
    }

    [Fact]
    public async Task A03_FifoDeadlineAndOfferSurviveAHostRestartInPostgres()
    {
        var database = await NewDemoAsync();
        Guid firstBooking, secondBooking, first, second;
        var clock = Clock();
        await using (var factory = BookingApi.Create(database, clock, Demo))
        {
            using var client = factory.CreateClient();
            (firstBooking, first) = await BuyWashAsync(client);
            clock.Advance(TimeSpan.FromSeconds(10));
            (secondBooking, second) = await BuyWashAsync(client);

            Assert.Equal("wash|1890|to_be_confirmed|washer|null|12:01:00", Describe(await ItemAsync(client, firstBooking, first)));
            Assert.Equal("wash|1890|queued|null|1|null", Describe(await ItemAsync(client, secondBooking, second)));
            Assert.Equal(["dryer-01|available|null|False", $"washer-01|reserved|{first}|False"], await MachinesAsync(client));
        }

        var persisted = await database.RowsAsync(StateSql);
        var restarted = Clock(T0.AddSeconds(60));
        await using var again = BookingApi.Create(database, restarted, Demo);
        using var api2 = again.CreateClient();

        // Depois do reinício o estado vem do PostgreSQL; a leitura no prazo exato ainda não expira nada.
        Assert.Equal("wash|1890|to_be_confirmed|washer|null|12:01:00", Describe(await ItemAsync(api2, firstBooking, first)));
        Assert.Equal(persisted, await database.RowsAsync(StateSql));

        var expiredPath = $"/api/v1/bookings/{firstBooking}/items/{first}/confirm";
        using var expired = await api2.PostAsync(expiredPath, null);

        await ProblemAssert.HasProblemAsync(expired, HttpStatusCode.Gone, "MACHINE_OFFER_EXPIRED", expiredPath);
        Assert.Equal("wash|1890|to_be_confirmed|washer|null|12:02:00", Describe(await ItemAsync(api2, secondBooking, second)));
        Assert.Equal("wash|1890|queued|null|1|null", Describe(await ItemAsync(api2, firstBooking, first)));
        Assert.True(
            await database.ScalarAsync<bool>($"SELECT (SELECT queue_ticket FROM service_request_items WHERE id = '{first}') > (SELECT queue_ticket FROM service_request_items WHERE id = '{second}')"),
            "a oferta vencida volta ao fim da fila com ticket novo");

        restarted.SetUtcNow(T0.AddSeconds(119));
        using var confirmed = await api2.PostAsync($"/api/v1/bookings/{secondBooking}/items/{second}/confirm", null);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("wash|1890|in_progress|washer|null|null", Describe(await BookingApi.JsonAsync(confirmed)));
        Assert.Equal(["dryer-01|available|null|False", $"washer-01|running|{second}|False"], await MachinesAsync(api2));
        Assert.Equal(["debit|null|18.90|31.10", "debit|null|18.90|12.20"], await database.RowsAsync(LedgerSql));
        Assert.Equal(1220L, await BalanceAsync(api2));
    }

    [Fact]
    public async Task A04_CancelBeforeStartRefundsTheHistoricalPriceExactlyOnce()
    {
        var database = await NewDemoAsync();
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        using var created = await BuyAsync(client, Guid.NewGuid(), WashAndDry);
        var booking = await BookingApi.JsonAsync(created);
        var bookingId = booking.GetProperty("bookingId").GetGuid();
        var wash = ItemId(booking, "wash");
        var dry = ItemId(booking, "dry");
        await database.ExecuteAsync("UPDATE services SET current_price = 25.00 WHERE code = 'wash'");
        clock.Advance(TimeSpan.FromSeconds(10));

        var cancelled = await CancelAsync(client, bookingId, wash);
        clock.Advance(TimeSpan.FromSeconds(10));
        var repeated = await CancelAsync(client, bookingId, wash);

        Assert.Equal("wash|1890|cancelled|null|null|null|1890|2910", cancelled);
        Assert.Equal(cancelled, repeated);
        Assert.Equal(["dryer-01|available|null|False", "washer-01|available|null|False"], await MachinesAsync(client));
        Assert.Equal(["debit|null|39.80|10.20", $"refund|{wash}|18.90|29.10"], await database.RowsAsync(LedgerSql));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("dry|2090|cancelled|null|null|null|2090|5000", await CancelAsync(client, bookingId, dry));

        Assert.Equal(5000L, await BalanceAsync(client));
        using var ledger = JsonDocument.Parse(await client.GetStringAsync("/api/v1/wallet/transactions"));
        Assert.Equal(
            [$"refund|{dry}|2090|5000", $"refund|{wash}|1890|2910", "debit|null|3980|1020"],
            ledger.RootElement.GetProperty("items").EnumerateArray().Select(entry => string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.GetProperty("kind").GetString()}|{(entry.GetProperty("itemId").ValueKind == JsonValueKind.Null ? "null" : entry.GetProperty("itemId").GetGuid().ToString())}|{entry.GetProperty("amountCents").GetInt64()}|{entry.GetProperty("balanceAfterCents").GetInt64()}")));
        Assert.Equal(["50.00|2|39.80"], await database.RowsAsync(
            "SELECT (SELECT balance FROM wallets) || '|' || (SELECT count(*) FROM wallet_transactions WHERE kind = 'refund') || '|' || (SELECT sum(amount) FROM wallet_transactions WHERE kind = 'refund')"));
    }

    [Fact]
    public async Task A05_DefectCompletionAndRestoreKeepMachineItemAndLedgerCoherent()
    {
        var database = await NewDemoAsync();
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (firstBooking, first) = await BuyWashAsync(client);
        clock.Advance(TimeSpan.FromSeconds(5));
        await AssertOkAsync(client.PostAsync($"/api/v1/bookings/{firstBooking}/items/{first}/confirm", null));

        using var reported = await client.PostAsync($"/api/v1/bookings/{firstBooking}/items/{first}/report-issue", null);

        Assert.Equal(HttpStatusCode.Accepted, reported.StatusCode);
        var report = await BookingApi.JsonAsync(reported);
        Assert.Equal("reported", report.GetProperty("status").GetString());
        var incident = report.GetProperty("incidentId").GetGuid();
        Assert.Equal(3110L, await BalanceAsync(client));
        Assert.Equal(["debit|null|18.90|31.10"], await database.RowsAsync(LedgerSql));

        clock.Advance(TimeSpan.FromSeconds(5));
        var (secondBooking, second) = await BuyWashAsync(client);
        Assert.Equal("wash|1890|queued|null|1|null", Describe(await ItemAsync(client, secondBooking, second)));
        clock.Advance(TimeSpan.FromSeconds(10));

        var confirmPath = $"/api/v1/simulation/incidents/{incident}/confirm";
        var confirmed = await BookingApi.JsonAsync(await AssertOkAsync(client.PostAsync(confirmPath, null)));
        var repeated = await BookingApi.JsonAsync(await AssertOkAsync(client.PostAsync(confirmPath, null)));

        foreach (var body in new[] { confirmed, repeated })
        {
            Assert.Equal((incident, "confirmed"), (body.GetProperty("incidentId").GetGuid(), body.GetProperty("status").GetString()));
            Assert.Equal("washer-01|out_of_service|null|False", Machine(body.GetProperty("machine")));
            Assert.Equal("wash|1890|failed|null|null|null", Describe(body.GetProperty("item")));
        }

        Assert.Equal(3110L, await BalanceAsync(client));
        Assert.Equal(["debit|null|18.90|31.10", "debit|null|18.90|12.20", $"refund|{first}|18.90|31.10"], await database.RowsAsync(LedgerSql));
        using (var services = JsonDocument.Parse(await client.GetStringAsync("/api/v1/services")))
        {
            var washService = services.RootElement.GetProperty("services").EnumerateArray().Single(service => service.GetProperty("code").GetString() == "wash");
            Assert.Equal((false, "NO_OPERATIONAL_MACHINE"), (washService.GetProperty("canBeBooked").GetBoolean(), washService.GetProperty("unavailabilityReason").GetString()));
        }

        using var refused = await BuyAsync(client, Guid.NewGuid(), Wash);
        await ProblemAssert.HasProblemAsync(refused, HttpStatusCode.Conflict, "NO_OPERATIONAL_MACHINE", "/api/v1/bookings");
        Assert.Equal("wash|1890|queued|null|1|null", Describe(await ItemAsync(client, secondBooking, second)));

        clock.Advance(TimeSpan.FromSeconds(10));
        var restored = await BookingApi.JsonAsync(await AssertOkAsync(client.PostAsync($"/api/v1/simulation/machines/{WasherId}/restore", null)));

        Assert.Equal($"washer-01|reserved|{second}|False", Machine(restored.GetProperty("machine")));
        Assert.Equal("wash|1890|to_be_confirmed|washer|null|12:01:30", Describe(restored.GetProperty("item")));

        clock.Advance(TimeSpan.FromSeconds(10));
        await AssertOkAsync(client.PostAsync($"/api/v1/bookings/{secondBooking}/items/{second}/confirm", null));
        clock.Advance(TimeSpan.FromSeconds(10));
        var completed = await BookingApi.JsonAsync(await AssertOkAsync(client.PostAsync($"/api/v1/simulation/machines/{WasherId}/complete", null)));

        Assert.Equal("washer-01|available|null|False", Machine(completed.GetProperty("machine")));
        Assert.Equal("wash|1890|completed|null|null|null", Describe(completed.GetProperty("item")));
        foreach (var command in new[] { "complete", "breakdown" })
        {
            var path = $"/api/v1/simulation/machines/{WasherId}/{command}";
            using var again = await client.PostAsync(path, null);
            await ProblemAssert.HasProblemAsync(again, HttpStatusCode.Conflict, "INVALID_STATE_TRANSITION", path);
        }

        Assert.Equal(3110L, await BalanceAsync(client));
        Assert.Equal(["debit|null|18.90|31.10", "debit|null|18.90|12.20", $"refund|{first}|18.90|31.10"], await database.RowsAsync(LedgerSql));
        Assert.Equal([$"{first}|failed", $"{second}|completed"], await database.RowsAsync($"SELECT id || '|' || status FROM service_request_items ORDER BY CASE WHEN id = '{first}' THEN 0 ELSE 1 END"));
        Assert.Equal(["confirmed|1"], await database.RowsAsync("SELECT string_agg(status, ',') || '|' || count(*) FROM machine_incidents"));
    }

    [Fact]
    public async Task A06_DatabaseOrCollectorDownNeverFakesSuccess()
    {
        await using (var down = ApiFactory.Create(ApiFactory.UnreachableDatabase, Demo))
        {
            using var client = down.CreateClient();
            foreach (var path in new[] { "/health/live", "/health" })
            {
                using var live = await client.GetAsync(path);
                Assert.Equal((HttpStatusCode.OK, "Healthy"), (live.StatusCode, await live.Content.ReadAsStringAsync()));
            }

            using var ready = await client.GetAsync("/health/ready");
            Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), (ready.StatusCode, await ready.Content.ReadAsStringAsync()));
            using var wallet = await client.GetAsync("/api/v1/wallet");
            await ProblemAssert.HasProblemAsync(wallet, HttpStatusCode.ServiceUnavailable, "DATABASE_UNAVAILABLE", "/api/v1/wallet");
            using var purchase = await BuyAsync(client, Guid.NewGuid(), WashAndDry);
            var problem = await ProblemAssert.HasProblemAsync(purchase, HttpStatusCode.ServiceUnavailable, "DATABASE_UNAVAILABLE", "/api/v1/bookings");
            Assert.DoesNotContain("nao-vazar-down", problem.GetRawText(), StringComparison.Ordinal);
        }

        var database = await NewDemoAsync();
        var refusedCollector = $"http://127.0.0.1:{OtlpReceiver.FreeLoopbackPort()}/";
        var settings = new Dictionary<string, string>(Demo) { ["OpenTelemetry:OtlpEndpoint"] = refusedCollector };
        await using (var factory = BookingApi.Create(database, Clock(), settings))
        {
            using var client = factory.CreateClient();
            using var created = await BuyAsync(client, Guid.NewGuid(), WashAndDry);

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal(1020L, (await BookingApi.JsonAsync(created)).GetProperty("remainingBalanceCents").GetInt64());
            Assert.Equal("Healthy", await client.GetStringAsync("/health/ready"));
        }

        // O encerramento do host tenta exportar para o coletor recusado; a compra continua confirmada no banco.
        Assert.Equal(["debit|null|39.80|10.20"], await database.RowsAsync(LedgerSql));
    }

    [Fact]
    public async Task A07_GuideResetIsRefusedWhileTheApiRunsAndRestoresTheDemoAfterItStops()
    {
        var database = await NewDemoAsync();
        var target = Target(database);
        ProcessResult unconfirmed, whileRunning;

        // Como a API real, o host usa pool de conexões: as sessões ociosas `cicclo.api` continuam abertas entre requisições.
        var pooled = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = true }.ConnectionString;
        await using (var factory = ApiFactory.Create(pooled, Demo))
        {
            using var client = factory.CreateClient();
            using var created = await BuyAsync(client, Guid.NewGuid(), WashAndDry);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal(1020L, await BalanceAsync(client));

            unconfirmed = await api.ResetAsync(database.ConnectionString, confirmation: null);
            whileRunning = await api.ResetAsync(database.ConnectionString, target);

            Assert.Equal(1020L, await BalanceAsync(client));
        }

        // Parar o processo da API fecha o pool; no host em memória, o fechamento é explícito.
        NpgsqlConnection.ClearAllPools();
        Assert.Equal(2, unconfirmed.ExitCode);
        Assert.Contains($"Reset demo recusado: confirme o alvo exato com --confirm-reset {target}.", unconfirmed.Output, StringComparison.Ordinal);
        Assert.Equal(2, whileRunning.ExitCode);
        Assert.Contains("conexão(ões) ativa(s) da API/worker (cicclo.api) no alvo", whileRunning.Output, StringComparison.Ordinal);
        Assert.Equal(["debit|null|39.80|10.20"], await database.RowsAsync(LedgerSql));

        var reset = await api.ResetAsync(database.ConnectionString, target);

        Assert.True(reset.ExitCode == 0, reset.Output);
        Assert.Contains("Reset demo concluído: dados iniciais restaurados pelo seed.sql.", reset.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ResetDemoDatabaseFixture.Password, reset.Output, StringComparison.Ordinal);
        Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
        Assert.Equal(["0|0|0"], await database.RowsAsync("SELECT (SELECT count(*) FROM service_requests) || '|' || (SELECT count(*) FROM wallet_transactions) || '|' || (SELECT count(*) FROM machine_incidents)"));
        await using var after = BookingApi.Create(database, Clock(), Demo);
        using var fresh = after.CreateClient();
        Assert.Equal(5000L, await BalanceAsync(fresh));
        using var again = await BuyAsync(fresh, Guid.NewGuid(), WashAndDry);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(1020L, (await BookingApi.JsonAsync(again)).GetProperty("remainingBalanceCents").GetInt64());
    }

    /// <summary>Banco `cicclo` novo preparado pelo artefato publicado em `--bootstrap-demo`, exatamente o comando do guia.</summary>
    private async Task<TestDatabase> NewDemoAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        var bootstrap = await api.BootstrapAsync(database.ConnectionString);
        Assert.True(bootstrap.ExitCode == 0, bootstrap.Output);
        Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
        return database;
    }

    private static string Target(TestDatabase database)
    {
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        return $"{builder.Host}:{builder.Port}/{builder.Database}";
    }

    private static async Task<HttpResponseMessage> BuyAsync(HttpClient client, Guid key, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(CreateBookingEndpoint.IdempotencyKeyHeader, key.ToString());
        return await client.SendAsync(request);
    }

    /// <summary>Compra de uma lavagem: o item único é ativado na própria compra.</summary>
    private static async Task<(Guid Booking, Guid Item)> BuyWashAsync(HttpClient client)
    {
        using var response = await BuyAsync(client, Guid.NewGuid(), Wash);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var booking = await BookingApi.JsonAsync(response);
        return (booking.GetProperty("bookingId").GetGuid(), ItemId(booking, "wash"));
    }

    private static async Task<HttpResponseMessage> AssertOkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response;
    }

    /// <summary>Cancelamento: item|refundedAmountCents|remainingBalanceCents.</summary>
    private static async Task<string> CancelAsync(HttpClient client, Guid booking, Guid item)
    {
        var body = await BookingApi.JsonAsync(await AssertOkAsync(client.PostAsync($"/api/v1/bookings/{booking}/items/{item}/cancel", null)));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Describe(body.GetProperty("item"))}|{body.GetProperty("refundedAmountCents").GetInt64()}|{body.GetProperty("remainingBalanceCents").GetInt64()}");
    }

    private static async Task<JsonElement> ItemAsync(HttpClient client, Guid booking, Guid item)
    {
        var body = await BookingApi.JsonAsync(await AssertOkAsync(client.GetAsync($"/api/v1/bookings/{booking}")));
        return body.GetProperty("items").EnumerateArray().Single(candidate => candidate.GetProperty("itemId").GetGuid() == item);
    }

    private static async Task<long> BalanceAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/wallet"));
        return body.RootElement.GetProperty("balanceCents").GetInt64();
    }

    private static async Task<IReadOnlyList<string>> MachinesAsync(HttpClient client)
    {
        var body = await BookingApi.JsonAsync(await AssertOkAsync(client.GetAsync("/api/v1/machines")));
        return [.. body.GetProperty("machines").EnumerateArray().Select(Machine)];
    }

    private static Guid ItemId(JsonElement booking, string code) =>
        booking.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("serviceCode").GetString() == code).GetProperty("itemId").GetGuid();

    private static IReadOnlyList<string> Items(JsonElement booking) => [.. booking.GetProperty("items").EnumerateArray().Select(Describe)];

    /// <summary>Item: código|preço|status|máquina|posição na fila|prazo da oferta (HH:mm:ss UTC).</summary>
    private static string Describe(JsonElement item)
    {
        var machine = item.GetProperty("machineId");
        var expires = item.GetProperty("expiresAt");
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{item.GetProperty("serviceCode").GetString()}|{item.GetProperty("unitPriceCents").GetInt64()}|{item.GetProperty("status").GetString()}|"
            + $"{(machine.ValueKind == JsonValueKind.Null ? "null" : machine.GetGuid() == Guid.Parse(WasherId) ? "washer" : machine.GetGuid() == Guid.Parse(DryerId) ? "dryer" : "other")}|"
            + $"{(item.GetProperty("queuePosition").ValueKind == JsonValueKind.Null ? "null" : item.GetProperty("queuePosition").GetInt32().ToString(CultureInfo.InvariantCulture))}|"
            + $"{(expires.ValueKind == JsonValueKind.Null ? "null" : expires.GetDateTimeOffset().UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture))}");
    }

    /// <summary>Máquina: nome|estado|item ativo|ocupação sintética.</summary>
    private static string Machine(JsonElement machine) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{machine.GetProperty("name").GetString()}|{machine.GetProperty("state").GetString()}|{(machine.GetProperty("activeItemId").ValueKind == JsonValueKind.Null ? "null" : machine.GetProperty("activeItemId").GetGuid().ToString())}|{machine.GetProperty("isDemoOccupied").GetBoolean()}");
}
