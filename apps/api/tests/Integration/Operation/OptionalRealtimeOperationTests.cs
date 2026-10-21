using System.Net;
using System.Text;
using System.Text.Json;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Laundry.Notifications;
using Cicclo.Api.Tests.Support;
using Cicclo.Api.Wallet.ReadCaching;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Integration.Operation;

/// <summary>
/// Operação do recurso realtime opcional como o README documenta (RT-01..10 operacionais, E09/T4): base sem Hub, conexão
/// recebendo avisos coerentes com o REST, avisos perdidos recuperados só por GET (sem replay), avisos reenviados pelo
/// cliente sem efeito no servidor, profile sem Redis e combinações de flag/ambiente/falha de transporte preservando
/// finanças e saúde. PostgreSQL 18.1 real, host HTTP real e cliente SignalR 10.0.13 real.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E09")]
[Trait("Task", "T4")]
public sealed class OptionalRealtimeOperationTests(PostgresFixture postgres)
{
    private const string WashBody = """{"serviceCodes":["wash"],"expectedTotalCents":1890}""";
    private const string DryBody = """{"serviceCodes":["dry"],"expectedTotalCents":2090}""";
    private const string NegotiatePath = "/hubs/laundry/negotiate?negotiateVersion=1";
    private const string LedgerSql = "SELECT kind || '|' || amount || '|' || balance_after FROM wallet_transactions ORDER BY created_at, kind";

    private static readonly Dictionary<string, string> Limits = new()
    {
        ["Laundry:QueueWorkerIntervalSeconds"] = "3600",
        ["RateLimiting:BookingCreate:PermitLimit"] = "1000",
        ["RateLimiting:BookingReads:PermitLimit"] = "1000",
        ["RateLimiting:ItemCommands:PermitLimit"] = "1000",
        ["RateLimiting:ItemCancel:PermitLimit"] = "1000",
    };

    /// <summary>As três chaves do guia: Realtime__Enabled=true, Development (do host de teste) e Laundry__DemoMode=true.</summary>
    private static Dictionary<string, string> Settings(bool realtime = true, bool demoMode = true) =>
        new(Limits) { ["Realtime:Enabled"] = realtime ? "true" : "false", ["Laundry:DemoMode"] = demoMode ? "true" : "false" };

    [Fact]
    public async Task O01_BaseHostWithoutTheOptionalFlagsServesRestAndHealthWithoutHub()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Limits);
        using var http = factory.CreateClient();

        using var negotiate = await http.PostAsync(NegotiatePath, null);
        using var ready = await http.GetAsync("/health/ready");
        var openApi = await http.GetStringAsync("/openapi/v1.json");
        var booking = await PurchaseAsync(http, WashBody);
        var item = await ItemAsync(http, booking);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.OK), (negotiate.StatusCode, ready.StatusCode));
        Assert.DoesNotContain("/hubs/", openApi, StringComparison.Ordinal);
        Assert.Null(factory.Services.GetService<IHubContext<LaundryHub>>());
        Assert.Equal("to_be_confirmed", item.GetProperty("status").GetString());
        Assert.Equal(3110, await BalanceCentsAsync(http));
    }

    [Fact]
    public async Task O02_EnabledConnectionReceivesNoticesThatMatchTheRestState()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Settings());
        using var http = factory.CreateClient();
        await using var client = await RealtimeTestClient.ConnectAsync(factory);

        var booking = await PurchaseAsync(http, WashBody);

        var offered = await client.NextAsync();
        Assert.Equal("machine.offered", offered.Name);
        Assert.Equal("queue.updated", (await client.NextAsync()).Name);
        Assert.Equal(1L, (await PeekBookingAsync(client, booking.Booking)).Payload.GetProperty("version").GetInt64());
        Assert.Equal("wallet.updated", (await client.NextAsync()).Name);
        var item = await ItemAsync(http, booking);
        Assert.Equal(item.GetProperty("itemId").GetGuid(), offered.Payload.GetProperty("itemId").GetGuid());
        Assert.Equal(item.GetProperty("machineId").GetGuid(), offered.Payload.GetProperty("machineId").GetGuid());
        Assert.Equal(item.GetProperty("expiresAt").GetDateTime(), offered.Payload.GetProperty("expiresAt").GetDateTime());
        Assert.Equal("to_be_confirmed", item.GetProperty("status").GetString());
        Assert.Equal(3110, await BalanceCentsAsync(http));
    }

    [Fact]
    public async Task O03_NoticesMissedWhileDisconnectedAreNotReplayedAndRestRecoversTheState()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings());
        using var http = factory.CreateClient();
        var first = await RealtimeTestClient.ConnectAsync(factory);
        var booking = await PurchaseAsync(http, WashBody);
        await first.SkipThroughAsync("wallet.updated");
        await first.DisposeAsync();

        // Cancelamento enquanto ninguém escuta: seus avisos se perdem.
        clock.SetUtcNow(T0.AddSeconds(5));
        using (var cancel = await http.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Item}/cancel", null))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        await using var second = await RealtimeTestClient.ConnectAsync(factory);
        clock.SetUtcNow(T0.AddSeconds(10));
        var dry = await PurchaseAsync(http, DryBody);

        // Nada do cancelamento chega ao cliente novo: o primeiro aviso já é da compra seguinte.
        var next = await second.NextAsync();
        Assert.Equal(("machine.offered", dry.Item), (next.Name, next.Payload.GetProperty("itemId").GetGuid()));
        var recovered = await ItemAsync(http, booking);
        Assert.Equal("cancelled", recovered.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, recovered.GetProperty("machineId").ValueKind);
        Assert.Equal(2910, await BalanceCentsAsync(http));
        Assert.Equal(["debit|18.90|31.10", "refund|18.90|50.00", "debit|20.90|29.10"], await database.RowsAsync(LedgerSql));
    }

    [Fact]
    public async Task O04_DuplicatedOrReversedNoticesSentBackByAClientDoNotChangeServerStateAndRestStaysAuthoritative()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings());
        using var http = factory.CreateClient();
        await using var client = await RealtimeTestClient.ConnectAsync(factory);
        var booking = await PurchaseAsync(http, WashBody);
        await client.SkipThroughAsync("queue.updated");
        var created = await PeekBookingAsync(client, booking.Booking);
        await client.SkipThroughAsync("wallet.updated");
        clock.SetUtcNow(T0.AddSeconds(10));
        using (var confirm = await http.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Item}/confirm", null))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        }

        var started = await client.NextAsync();
        Assert.Equal((1L, 2L), (created.Payload.GetProperty("version").GetInt64(), started.Payload.GetProperty("version").GetInt64()));
        var state = await database.RowsAsync(StateSql);

        // Ordem inversa e duplicata, devolvidas ao Hub pelo cliente: não existe método que as aceite.
        foreach (var (name, payload) in new[] { started, created, created })
        {
            await Assert.ThrowsAsync<HubException>(() => client.Connection.InvokeAsync<object>(name, payload));
        }

        Assert.Equal(state, await database.RowsAsync(StateSql));
        Assert.Equal(2L, await database.ScalarAsync<long>($"SELECT revision FROM service_requests WHERE id = '{booking.Booking}'"));
        Assert.Equal("in_progress", (await ItemAsync(http, booking)).GetProperty("status").GetString());
        Assert.Equal(["debit|18.90|31.10"], await database.RowsAsync(LedgerSql));
    }

    [Fact]
    public async Task O05_RealtimeProfileWithoutRedisRunsTheWholeCycleAlone()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings());
        using var http = factory.CreateClient();
        await using var client = await RealtimeTestClient.ConnectAsync(factory);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        var booking = await PurchaseAsync(http, WashBody);
        await client.SkipThroughAsync("wallet.updated");
        clock.SetUtcNow(T0.AddSeconds(10));
        await AssertStatusAsync(HttpStatusCode.OK, http.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Item}/confirm", null));
        var started = await client.NextAsync();
        clock.SetUtcNow(T0.AddSeconds(20));
        await AssertStatusAsync(HttpStatusCode.OK, http.PostAsync($"/api/v1/simulation/machines/{WasherId}/complete", null));
        var completed = await client.NextAsync();
        using var ready = await http.GetAsync("/health/ready");

        Assert.Equal((2L, 3L), (started.Payload.GetProperty("version").GetInt64(), completed.Payload.GetProperty("version").GetInt64()));
        Assert.Equal("completed", (await ItemAsync(http, booking)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.IsType<DefaultHubLifetimeManager<LaundryHub>>(factory.Services.GetRequiredService<HubLifetimeManager<LaundryHub>>());
        Assert.Null(configuration.GetConnectionString("Redis"));
        // O cache opcional (E08) desligado não registra adaptador nem conexão Redis; o realtime não usa backplane Redis.
        Assert.Null(factory.Services.GetService<IWalletReadCache>());
        Assert.Null(factory.Services.GetService<StackExchange.Redis.IConnectionMultiplexer>());
        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name!.Contains("SignalR", StringComparison.OrdinalIgnoreCase)
                && assembly.GetName().Name!.Contains("Redis", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("failing-transport", true, true, "Development", HttpStatusCode.OK)]
    [InlineData("production", true, true, "Production", HttpStatusCode.NotFound)]
    [InlineData("demo-off", true, false, "Development", HttpStatusCode.NotFound)]
    [InlineData("flag-off", false, true, "Development", HttpStatusCode.NotFound)]
    public async Task O06_TransportFailureAndOptionalCombinationsPreserveMoneyAndHealth(
        string scenario, bool realtime, bool demoMode, string environment, HttpStatusCode negotiateStatus)
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Settings(realtime, demoMode)).WithWebHostBuilder(host =>
        {
            host.UseEnvironment(environment);
            if (scenario == "failing-transport")
            {
                host.ConfigureTestServices(services => services.AddSingleton<ILaundryNotifications>(new BrokenTransport()));
            }
        });
        using var http = factory.CreateClient();

        using var negotiate = await http.PostAsync(NegotiatePath, null);
        var booking = await PurchaseAsync(http, WashBody);
        await AssertStatusAsync(HttpStatusCode.OK, http.PostAsync($"/api/v1/bookings/{booking.Booking}/items/{booking.Item}/cancel", null));
        using var live = await http.GetAsync("/health/live");
        using var ready = await http.GetAsync("/health/ready");

        Assert.Equal(negotiateStatus, negotiate.StatusCode);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (live.StatusCode, ready.StatusCode));
        Assert.Equal(5000, await BalanceCentsAsync(http));
        Assert.Equal(["debit|18.90|31.10", "refund|18.90|50.00"], await database.RowsAsync(LedgerSql));
        Assert.Equal("cancelled|2", await database.ScalarAsync<string>(
            $"SELECT i.status || '|' || r.revision FROM service_request_items i JOIN service_requests r ON r.id = i.request_id WHERE i.id = '{booking.Item}'"));
    }

    private static async Task<(Guid Booking, Guid Item)> PurchaseAsync(HttpClient client, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(CreateBookingEndpoint.IdempotencyKeyHeader, Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await BookingApi.JsonAsync(response);
        return (json.GetProperty("bookingId").GetGuid(), json.GetProperty("items")[0].GetProperty("itemId").GetGuid());
    }

    private static async Task<JsonElement> ItemAsync(HttpClient client, (Guid Booking, Guid Item) booking)
    {
        using var response = await client.GetAsync($"/api/v1/bookings/{booking.Booking}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await BookingApi.JsonAsync(response);
        return json.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("itemId").GetGuid() == booking.Item);
    }

    private static async Task<long> BalanceCentsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/wallet");
        return (await BookingApi.JsonAsync(response)).GetProperty("balanceCents").GetInt64();
    }

    /// <summary>Próximo aviso, conferido como booking.updated do booking indicado.</summary>
    private static async Task<(string Name, JsonElement Payload)> PeekBookingAsync(RealtimeTestClient client, Guid bookingId)
    {
        var notice = await client.NextAsync();
        Assert.Equal(("booking.updated", bookingId), (notice.Name, notice.Payload.GetProperty("bookingId").GetGuid()));
        return notice;
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call;
        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>Transporte realtime fora do ar: toda publicação falha.</summary>
    private sealed class BrokenTransport : ILaundryNotifications
    {
        public Task PublishAsync(IReadOnlyList<LaundryNotification> notifications, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("transporte realtime indisponível"));
    }
}
