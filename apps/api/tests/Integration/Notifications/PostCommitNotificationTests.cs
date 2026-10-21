using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cicclo.Api.Bookings;
using Cicclo.Api.Bookings.CancelItem;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Laundry;
using Cicclo.Api.Laundry.Notifications;
using Cicclo.Api.Tests.Integration.Bookings;
using Cicclo.Api.Tests.Integration.Laundry;
using Cicclo.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Integration.Notifications;

/// <summary>
/// Avisos realtime ligados aos commits reais (RT-01..10, E09/T3) sobre PostgreSQL 18.1, host HTTP real e cliente SignalR
/// 10.0.13 real: barreira Realtime.Enabled AND Development AND DemoMode, nada publicado em rollback ou commit
/// desconhecido, versão = revisão confirmada no banco, worker, falha/timeout do emissor sem efeito no resultado REST ou
/// financeiro e operação sem Redis/backplane. Uma sonda no lugar do emissor lê o banco por outra conexão no instante da
/// publicação: prova que o commit já é visível e que o lock da wallet já foi liberado.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E09")]
[Trait("Task", "T3")]
public sealed class PostCommitNotificationTests(PostgresFixture postgres)
{
    private const string WashBody = """{"serviceCodes":["wash"],"expectedTotalCents":1890}""";
    private const string HookedApp = "cicclo-e09-t3-hooked";
    private const string NegotiatePath = "/hubs/laundry/negotiate?negotiateVersion=1";
    private const string LedgerSql = "SELECT kind || '|' || amount || '|' || balance_after FROM wallet_transactions ORDER BY created_at, kind";
    private const string WalletLockSql = $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE";

    /// <summary>Simulação ligada, worker do host parado (1 h) e limites altos: só o comando do passo muda o banco.</summary>
    private static readonly Dictionary<string, string> BaseSettings = new()
    {
        ["Laundry:DemoMode"] = "true",
        ["Laundry:QueueWorkerIntervalSeconds"] = "3600",
        ["RateLimiting:BookingCreate:PermitLimit"] = "1000",
        ["RateLimiting:BookingReads:PermitLimit"] = "1000",
        ["RateLimiting:ItemCommands:PermitLimit"] = "1000",
        ["RateLimiting:ItemCancel:PermitLimit"] = "1000",
    };

    private static Dictionary<string, string> Realtime(params (string Key, string Value)[] overrides)
    {
        var settings = new Dictionary<string, string>(BaseSettings) { ["Realtime:Enabled"] = "true" };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return settings;
    }

    [Fact]
    public async Task R01_FlagOffLeavesHubUnmappedWithoutSignalRWhileRestPurchaseWorks()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), BaseSettings);
        using var client = factory.CreateClient();

        using var negotiate = await client.PostAsync(NegotiatePath, null);
        using var hub = await client.GetAsync(LaundryHub.Path);
        var booking = await PurchaseAsync(client);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (negotiate.StatusCode, hub.StatusCode));
        Assert.Null(factory.Services.GetService<ILaundryNotifications>());
        Assert.Null(factory.Services.GetService<LaundryChangeNotifier>());
        Assert.Null(factory.Services.GetService<IHubContext<LaundryHub>>());
        Assert.Equal(["debit|18.90|31.10"], await database.RowsAsync(LedgerSql));
        Assert.Equal("to_be_confirmed", await database.ScalarAsync<string>($"SELECT status FROM service_request_items WHERE id = '{booking.Item}'"));
    }

    [Theory]
    [InlineData("true", "Production", "true", HttpStatusCode.NotFound)]
    [InlineData("true", "Staging", "true", HttpStatusCode.NotFound)]
    [InlineData("true", "Development", "false", HttpStatusCode.NotFound)]
    [InlineData("false", "Development", "true", HttpStatusCode.NotFound)]
    [InlineData("true", "Development", "true", HttpStatusCode.OK)]
    public async Task R02_HubIsMappedOnlyForEnabledFlagInDevelopmentWithDemoMode(string enabled, string environment, string demoMode, HttpStatusCode expected)
    {
        var database = await ReadyAsync(postgres);
        var settings = Realtime(("Realtime:Enabled", enabled), ("Laundry:DemoMode", demoMode));
        await using var factory = BookingApi.Create(database, Clock(), settings).WithWebHostBuilder(host => host.UseEnvironment(environment));
        using var client = factory.CreateClient();

        using var negotiate = await client.PostAsync(NegotiatePath, null);

        Assert.Equal(expected, negotiate.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK, factory.Services.GetService<ILaundryNotifications>() is not null);
        if (expected == HttpStatusCode.OK)
        {
            using var body = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync());
            Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("connectionToken").GetString()));
            var openApi = await client.GetStringAsync("/openapi/v1.json");
            Assert.DoesNotContain("/hubs/", openApi, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task R03_RolledBackOrUnknownCommitsPublishNothingAndTheNextCommitIsTheFirstEventSeen()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var probe = new ProbeState(database);
        await using var factory = Host(database, clock, Realtime(), probe);
        using var http = factory.CreateClient();
        await using var client = await ConnectAsync(factory);
        var booking = await PurchaseAsync(http);
        await client.SkipThroughAsync("wallet.updated");
        var state = await database.RowsAsync(StateSql);

        // Conexão derrubada no servidor logo antes do COMMIT: cancelamento revertido, nada a anunciar.
        var hooked = new HookedLaundrySessions(Sessions(database, HookedApp), () => TerminateAsync(database, HookedApp));
        var cancel = ActivatorUtilities.CreateInstance<CancelItem>(factory.Services, hooked);
        Assert.NotNull(await Record.ExceptionAsync(() => cancel.ExecuteAsync(booking.Booking, booking.Item, CancellationToken.None)));
        Assert.Equal(state, await database.RowsAsync(StateSql));

        // Commit real sem confirmação: a compra existe, mas o resultado desconhecido nunca é anunciado.
        var unknown = new ScriptedBookingSessions(new PostgresCreateBookingSessions(database.ContextFactory(HookedApp)))
        {
            OnCommit = async (_, commit) =>
            {
                await commit();
                throw new CreateBookingStoreException(CreateBookingStoreFailure.CommitOutcomeUnknown, "confirmação perdida depois do commit");
            },
        };
        var purchase = await ActivatorUtilities.CreateInstance<CreateBooking>(factory.Services, unknown)
            .ExecuteAsync(Guid.NewGuid(), BookingIntent.Create(["dry"], null, 2090, 10).Intent!, CancellationToken.None);
        Assert.Equal(CreateBookingOutcome.Created, purchase.Outcome);
        Assert.Equal(["debit|18.90|31.10", "debit|20.90|10.20"], await database.RowsAsync(LedgerSql));
        Assert.Single(probe.Batches);

        using (var committed = await http.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Item}/cancel", null))
        {
            Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        }

        var first = await client.NextAsync();
        Assert.Equal("booking.updated", first.Name);
        Assert.Equal((booking.Booking, 2L), (first.Payload.GetProperty("bookingId").GetGuid(), first.Payload.GetProperty("version").GetInt64()));
        Assert.Equal("wallet.updated", (await client.NextAsync()).Name);
        Assert.Equal(2, probe.Batches.Count);
        Assert.Equal(["booking.updated", "wallet.updated"], probe.Batches[1].Names);
    }

    [Fact]
    public async Task R04_NewPurchaseAnnouncesOfferQueueRevisionOneAndWalletOnlyAfterTheCommit()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var probe = new ProbeState(database);
        await using var factory = Host(database, clock, Realtime(), probe);
        using var http = factory.CreateClient();
        await using var client = await ConnectAsync(factory);

        var booking = await PurchaseAsync(http);

        var offered = await client.NextAsync();
        Assert.Equal("machine.offered", offered.Name);
        Assert.Equal(booking.Item, offered.Payload.GetProperty("itemId").GetGuid());
        Assert.Equal(Guid.Parse(WasherId), offered.Payload.GetProperty("machineId").GetGuid());
        AssertUtc(T0.AddSeconds(60), offered.Payload.GetProperty("expiresAt"));
        AssertUtc(T0, offered.Payload.GetProperty("occurredAt"));
        Assert.Equal(
            $"{WasherId}|12:01:00.000000",
            await database.ScalarAsync<string>($"SELECT machine_id || '|' || to_char(expires_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') FROM service_request_items WHERE id = '{booking.Item}'"));
        var queue = await client.NextAsync();
        Assert.Equal(("queue.updated", "washer"), (queue.Name, queue.Payload.GetProperty("machineType").GetString()));
        var updated = await client.NextAsync();
        Assert.Equal("booking.updated", updated.Name);
        Assert.Equal(7, updated.Payload.GetProperty("eventId").GetGuid().Version);
        Assert.Equal(booking.Booking, updated.Payload.GetProperty("bookingId").GetGuid());
        Assert.Equal(BookingRevision.Initial, updated.Payload.GetProperty("version").GetInt64());
        AssertUtc(T0, updated.Payload.GetProperty("occurredAt"));
        var wallet = await client.NextAsync();
        Assert.Equal("wallet.updated", wallet.Name);
        AssertUtc(T0, wallet.Payload.GetProperty("occurredAt"));

        // No instante da publicação, outra conexão já via a compra confirmada na mesma revisão, e a wallet estava livre.
        var batch = Assert.Single(probe.Batches);
        Assert.Equal(["machine.offered", "queue.updated", "booking.updated", "wallet.updated"], batch.Names);
        Assert.Equal([(booking.Booking, 1L, 1L)], batch.Revisions);
        Assert.True(batch.WalletLockFree);
    }

    [Fact]
    public async Task R05_ReplayAndRepeatedCommandsWithoutChangeAnnounceNothing()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var probe = new ProbeState(database);
        await using var factory = Host(database, clock, Realtime(), probe);
        using var http = factory.CreateClient();
        await using var client = await ConnectAsync(factory);
        var key = Guid.NewGuid();
        var booking = await PurchaseAsync(http, key);
        await client.SkipThroughAsync("wallet.updated");

        using (var replay = await PostBookingAsync(http, key, WashBody))
        using (var activateAgain = await http.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Item}/activate", null))
        using (var detail = await http.GetAsync($"/api/v1/bookings/{booking.Booking}"))
        {
            Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK), (replay.StatusCode, activateAgain.StatusCode, detail.StatusCode));
        }

        clock.SetUtcNow(T0.AddSeconds(10));
        using (var confirm = await http.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Item}/confirm", null))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        }

        var next = await client.NextAsync();
        Assert.Equal(("booking.updated", 2L), (next.Name, next.Payload.GetProperty("version").GetInt64()));
        AssertUtc(T0.AddSeconds(10), next.Payload.GetProperty("occurredAt"));
        Assert.Equal(2, probe.Batches.Count);
        Assert.Equal(["booking.updated"], probe.Batches[1].Names);
        Assert.Equal([(booking.Booking, 2L, 2L)], probe.Batches[1].Revisions);
        Assert.Equal(["debit|18.90|31.10"], await database.RowsAsync(LedgerSql));
    }

    [Fact]
    public async Task R06_SeveralChangesOfOneBookingInOneCommitAnnounceASingleConfirmedRevision()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var probe = new ProbeState(database);
        await using var factory = Host(database, clock, Realtime(), probe);
        using var http = factory.CreateClient();
        await using var client = await ConnectAsync(factory);
        var booking = await PurchaseAsync(http, body: """{"serviceCodes":["wash","wash"],"preferredFirstServiceCode":"wash","expectedTotalCents":3780}""");
        var first = await ItemInAsync(database, booking.Booking, "to_be_confirmed");
        var second = await ItemInAsync(database, booking.Booking, "pending_activation");
        clock.SetUtcNow(T0.AddSeconds(5));
        await AssertStatusAsync(HttpStatusCode.OK, http.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{second}/activate", null));
        clock.SetUtcNow(T0.AddSeconds(10));
        await AssertStatusAsync(HttpStatusCode.OK, http.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{first}/confirm", null));
        Assert.Equal(
            ["machine.offered|queue.updated|booking.updated|wallet.updated", "queue.updated|booking.updated", "booking.updated"],
            probe.Batches.Select(batch => string.Join('|', batch.Names)));
        for (var index = 0; index < 4 + 2 + 1; index++)
        {
            await client.NextAsync();
        }

        // Conclusão do primeiro item e oferta do segundo, do mesmo booking, no mesmo commit.
        clock.SetUtcNow(T0.AddSeconds(20));
        await AssertStatusAsync(HttpStatusCode.OK, http.PostAsync($"/api/v1/simulation/machines/{WasherId}/complete", null));

        var names = new List<string>();
        var received = new List<(string Name, JsonElement Payload)>();
        for (var index = 0; index < 3; index++)
        {
            received.Add(await client.NextAsync());
            names.Add(received[^1].Name);
        }

        Assert.Equal(["machine.offered", "queue.updated", "booking.updated"], names);
        Assert.Equal(second, received[0].Payload.GetProperty("itemId").GetGuid());
        AssertUtc(T0.AddSeconds(80), received[0].Payload.GetProperty("expiresAt"));
        Assert.Equal((booking.Booking, 4L), (received[2].Payload.GetProperty("bookingId").GetGuid(), received[2].Payload.GetProperty("version").GetInt64()));
        Assert.Equal([(booking.Booking, 4L, 4L)], probe.Batches[^1].Revisions);
        Assert.Equal(4L, await database.ScalarAsync<long>($"SELECT revision FROM service_requests WHERE id = '{booking.Booking}'"));
    }

    [Fact]
    public async Task R07_HostedWorkerAnnouncesExpiredAndOfferedWithPersistedValuesAndRevisions()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var probe = new ProbeState(database);
        await using var factory = Host(database, clock, Realtime(("Laundry:QueueWorkerIntervalSeconds", "60")), probe);
        using var http = factory.CreateClient();
        await using var client = await ConnectAsync(factory);
        var first = await PurchaseAsync(http);
        clock.SetUtcNow(T0.AddSeconds(1));
        var second = await PurchaseAsync(http);
        await client.SkipThroughAsync("wallet.updated");
        await client.SkipThroughAsync("wallet.updated");

        // O tick de 60 s do worker real: a oferta de A venceu em 12:01:00, A volta ao fim da fila e B recebe a lavadora.
        clock.SetUtcNow(T0.AddSeconds(61));

        var expired = await client.NextAsync();
        Assert.Equal(("machine.offer_expired", first.Item), (expired.Name, expired.Payload.GetProperty("itemId").GetGuid()));
        AssertUtc(T0.AddSeconds(61), expired.Payload.GetProperty("occurredAt"));
        var offered = await client.NextAsync();
        Assert.Equal(("machine.offered", second.Item), (offered.Name, offered.Payload.GetProperty("itemId").GetGuid()));
        Assert.Equal(Guid.Parse(WasherId), offered.Payload.GetProperty("machineId").GetGuid());
        AssertUtc(T0.AddSeconds(121), offered.Payload.GetProperty("expiresAt"));
        AssertUtc(T0.AddSeconds(61), offered.Payload.GetProperty("occurredAt"));
        Assert.Equal("queue.updated", (await client.NextAsync()).Name);
        var versions = new Dictionary<Guid, long>();
        for (var index = 0; index < 2; index++)
        {
            var updated = await client.NextAsync();
            Assert.Equal("booking.updated", updated.Name);
            versions[updated.Payload.GetProperty("bookingId").GetGuid()] = updated.Payload.GetProperty("version").GetInt64();
        }

        Assert.Equal(new Dictionary<Guid, long> { [first.Booking] = 2, [second.Booking] = 2 }, versions);
        const string ItemSql = "SELECT status || '|' || coalesce(to_char(expires_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US'), 'null') FROM service_request_items WHERE id = ";
        Assert.Equal("queued|null", await database.ScalarAsync<string>($"{ItemSql}'{first.Item}'"));
        Assert.Equal("to_be_confirmed|12:02:01.000000", await database.ScalarAsync<string>($"{ItemSql}'{second.Item}'"));
        Assert.All(probe.Batches[^1].Revisions, revision => Assert.Equal(revision.Payload, revision.Database));
        Assert.True(probe.Batches[^1].WalletLockFree);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R08_PublisherFailureOrTimeoutKeepsTheConfirmedStatusesAndMoney(bool hangs)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var logs = new CapturingLoggerProvider();
        var failing = new FailingNotifications(hangs);
        await using var factory = Host(database, clock, Realtime(), failing, logs);
        using var http = factory.CreateClient();

        var booking = await PurchaseAsync(http);
        clock.SetUtcNow(T0.AddSeconds(61));
        using (var expired = await http.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Item}/confirm", null))
        {
            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
            Assert.Contains("MACHINE_OFFER_EXPIRED", await expired.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        clock.SetUtcNow(T0.AddSeconds(70));
        using var cancel = await http.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Item}/cancel", null);
        using var wallet = await http.GetAsync("/api/v1/wallet");

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(1890, (await BookingApi.JsonAsync(cancel)).GetProperty("refundedAmountCents").GetInt64());
        Assert.Equal(5000, (await BookingApi.JsonAsync(wallet)).GetProperty("balanceCents").GetInt64());
        Assert.Equal(["debit|18.90|31.10", "refund|18.90|50.00"], await database.RowsAsync(LedgerSql));
        Assert.Equal("cancelled|3", await database.ScalarAsync<string>(
            $"SELECT i.status || '|' || r.revision FROM service_request_items i JOIN service_requests r ON r.id = i.request_id WHERE i.id = '{booking.Item}'"));
        Assert.Equal(3, failing.Attempts);
        Assert.Equal(hangs ? 3 : 0, failing.Cancelled);
        var warnings = logs.Entries.Where(log => log.EventId.Id == 900).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, warning => Assert.Equal(LogLevel.Warning, warning.Level));
        Assert.All(warnings, warning => Assert.IsAssignableFrom(hangs ? typeof(OperationCanceledException) : typeof(IOException), warning.Exception));
    }

    [Fact]
    public async Task R09_HubWithoutClientsOrWithADisconnectedClientKeepsPurchaseAndRefund()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = Host(database, clock, Realtime());
        using var http = factory.CreateClient();

        var lonely = await PurchaseAsync(http);
        await AssertStatusAsync(HttpStatusCode.OK, http.PostAsync($"/api/v1/bookings/{lonely.Booking}/items/{lonely.Item}/cancel", null));
        var gone = await ConnectAsync(factory);
        await gone.DisposeAsync();
        clock.SetUtcNow(T0.AddSeconds(5));
        var after = await PurchaseAsync(http, body: """{"serviceCodes":["dry"],"expectedTotalCents":2090}""");
        await AssertStatusAsync(HttpStatusCode.OK, http.PostAsync($"/api/v1/bookings/{after.Booking}/items/{after.Item}/cancel", null));

        Assert.Equal(
            ["debit|18.90|31.10", "refund|18.90|50.00", "debit|20.90|29.10", "refund|20.90|50.00"],
            await database.RowsAsync(LedgerSql));
        Assert.Equal(["cancelled", "cancelled"], await database.RowsAsync("SELECT status FROM service_request_items ORDER BY id"));
    }

    [Fact]
    public async Task R10_RealtimeOnlyRunsOnTheInMemoryLifetimeManagerWithoutRedisOrBackplane()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = Host(database, Clock(), Realtime());
        using var http = factory.CreateClient();
        await using var client = await ConnectAsync(factory);

        var booking = await PurchaseAsync(http);

        await client.SkipThroughAsync("queue.updated");
        var updated = await client.NextAsync();
        Assert.Equal(("booking.updated", booking.Booking), (updated.Name, updated.Payload.GetProperty("bookingId").GetGuid()));
        Assert.IsType<DefaultHubLifetimeManager<LaundryHub>>(factory.Services.GetRequiredService<HubLifetimeManager<LaundryHub>>());
        Assert.IsType<LaundryNotificationPublisher>(factory.Services.GetRequiredService<ILaundryNotifications>());
        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name!.Contains("Redis", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(Program).Assembly.GetReferencedAssemblies(),
            reference => reference.Name!.Contains("Redis", StringComparison.OrdinalIgnoreCase));
    }

    private static WebApplicationFactory<Program> Host(
        TestDatabase database, FakeTimeProvider clock, Dictionary<string, string> settings, object? notifications = null, CapturingLoggerProvider? logs = null) =>
        BookingApi.Create(database, clock, settings).WithWebHostBuilder(host =>
        {
            if (logs is not null)
            {
                host.ConfigureLogging(logging => logging.AddProvider(logs));
            }

            host.ConfigureTestServices(services =>
            {
                switch (notifications)
                {
                    case ProbeState probe:
                        services.AddSingleton<ILaundryNotifications>(provider =>
                            new ProbeNotifications(new LaundryNotificationPublisher(provider.GetRequiredService<IHubContext<LaundryHub>>()), probe));
                        break;
                    case ILaundryNotifications replacement:
                        services.AddSingleton(replacement);
                        break;
                }
            });
        });

    /// <summary>
    /// Cliente real por long polling sobre o TestServer. A primeira invocação recusada funciona como barreira: o servidor só
    /// despacha invocações depois de registrar a conexão, então Clients.All já a inclui.
    /// </summary>
    private static async Task<RealtimeClient> ConnectAsync(WebApplicationFactory<Program> factory)
    {
        var server = factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, LaundryHub.Path), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();
        var client = new RealtimeClient(connection);
        await connection.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<object>("Ready"));
        return client;
    }

    private static async Task<(Guid Booking, Guid Item)> PurchaseAsync(HttpClient client, Guid? key = null, string body = WashBody)
    {
        using var response = await PostBookingAsync(client, key ?? Guid.NewGuid(), body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await BookingApi.JsonAsync(response);
        return (json.GetProperty("bookingId").GetGuid(), json.GetProperty("items")[0].GetProperty("itemId").GetGuid());
    }

    private static Task<HttpResponseMessage> PostBookingAsync(HttpClient client, Guid key, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(CreateBookingEndpoint.IdempotencyKeyHeader, key.ToString());
        return client.SendAsync(request);
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call;
        Assert.Equal(expected, response.StatusCode);
    }

    private static Task<Guid> ItemInAsync(TestDatabase database, Guid booking, string status) =>
        database.ScalarAsync<Guid>($"SELECT id FROM service_request_items WHERE request_id = '{booking}' AND status = '{status}'");

    /// <summary>Data wire em UTC explícito (sufixo Z) e igual ao instante esperado, sem depender de cultura.</summary>
    private static void AssertUtc(DateTimeOffset expected, JsonElement value)
    {
        Assert.EndsWith("Z", value.GetString(), StringComparison.Ordinal);
        var parsed = value.GetDateTime();
        Assert.Equal((DateTimeKind.Utc, expected.UtcDateTime), (parsed.Kind, parsed));
    }

    private static async Task TerminateAsync(TestDatabase database, string applicationName) =>
        Assert.Equal(["true"], await database.RowsAsync(
            $"SELECT pg_terminate_backend(pid, {(int)SignalTimeout.TotalMilliseconds})::text FROM pg_stat_activity WHERE datname = current_database() AND application_name = '{applicationName}' AND state <> 'idle'"));

    private sealed class RealtimeClient : IAsyncDisposable
    {
        /// <summary>Nomes da tabela de eventos do design, literais: o cliente não herda as constantes do servidor.</summary>
        private static readonly string[] EventNames =
            ["booking.updated", "queue.updated", "machine.offered", "machine.offer_expired", "wallet.updated", "machine.failed"];

        private readonly HubConnection connection;
        private readonly Channel<(string Name, JsonElement Payload)> received = Channel.CreateUnbounded<(string, JsonElement)>();

        public RealtimeClient(HubConnection connection)
        {
            this.connection = connection;
            foreach (var name in EventNames)
            {
                connection.On<JsonElement>(name, payload => received.Writer.TryWrite((name, payload)));
            }
        }

        public async Task<(string Name, JsonElement Payload)> NextAsync() =>
            await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task SkipThroughAsync(string name)
        {
            while ((await NextAsync()).Name != name)
            {
            }
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    /// <summary>Lote visto pela sonda: nomes, (booking, versão do payload, revisão no banco) e se a wallet estava livre.</summary>
    private sealed record ProbeBatch(string[] Names, (Guid Booking, long Payload, long Database)[] Revisions, bool WalletLockFree);

    private sealed class ProbeState(TestDatabase database)
    {
        private readonly List<ProbeBatch> batches = [];

        public TestDatabase Database { get; } = database;

        public IReadOnlyList<ProbeBatch> Batches
        {
            get
            {
                lock (batches)
                {
                    return [.. batches];
                }
            }
        }

        public void Add(ProbeBatch batch)
        {
            lock (batches)
            {
                batches.Add(batch);
            }
        }
    }

    /// <summary>
    /// Emissor de produção observado: antes de enviar, lê por outra conexão a revisão de cada booking anunciado e tenta
    /// bloquear a wallet sem esperar (NOWAIT). Publicar antes do commit ou dentro do lock aparece aqui, não por acaso.
    /// </summary>
    private sealed class ProbeNotifications(ILaundryNotifications inner, ProbeState probe) : ILaundryNotifications
    {
        public async Task PublishAsync(IReadOnlyList<LaundryNotification> notifications, CancellationToken cancellationToken)
        {
            var revisions = new List<(Guid, long, long)>();
            foreach (var payload in notifications.Select(notification => notification.Payload).OfType<BookingUpdatedPayload>())
            {
                var stored = await probe.Database.RowsAsync($"SELECT revision FROM service_requests WHERE id = '{payload.BookingId}'");
                revisions.Add((payload.BookingId, payload.Version, stored.Count == 1 ? long.Parse(stored[0], System.Globalization.CultureInfo.InvariantCulture) : 0));
            }

            probe.Add(new([.. notifications.Select(notification => notification.EventName)], [.. revisions], await CanLockNowAsync(probe.Database, WalletLockSql)));
            await inner.PublishAsync(notifications, cancellationToken);
        }
    }

    /// <summary>Emissor que falha (IOException) ou nunca termina até o orçamento cancelar; conta tentativas e cancelamentos.</summary>
    private sealed class FailingNotifications(bool hangs) : ILaundryNotifications
    {
        private int attempts;
        private int cancelled;

        public int Attempts => Volatile.Read(ref attempts);

        public int Cancelled => Volatile.Read(ref cancelled);

        public async Task PublishAsync(IReadOnlyList<LaundryNotification> notifications, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attempts);
            if (!hangs)
            {
                throw new IOException("transporte realtime indisponível");
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
        }
    }
}
