using System.Net;
using System.Text;
using System.Text.Json;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Laundry.Notifications;
using Cicclo.Api.Tests.Support;
using Cicclo.Api.Wallet.ReadCaching;
using Microsoft.AspNetCore.Hosting;
using Cicclo.Api.Wallet.GetBalance;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Integration;

/// <summary>
/// Coexistência das duas extensões opcionais (E08 cache Redis + E09 SignalR) ligadas juntas no mesmo host Development com
/// DemoMode, sobre PostgreSQL 18.1 e Redis reais, ambos exclusivos (o Redis é desta classe, com prefixo de chaves
/// próprio). Compra e cancelamento removem a projeção da wallet depois do commit e antes de `wallet.updated`, entregam os
/// avisos esperados ao cliente SignalR real e o GET seguinte volta ao saldo do PostgreSQL. A porta do cache é o adaptador
/// Redis real atrasado na invalidação: se o aviso saísse antes dela, o cliente o receberia com a chave antiga ainda lá.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "INT")]
public sealed class OptionalIncrementsCoexistenceTests(PostgresFixture postgres, RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string WashBody = """{"serviceCodes":["wash"],"expectedTotalCents":1890}""";
    private const string LedgerSql = "SELECT kind || '|' || amount || '|' || balance_after FROM wallet_transactions ORDER BY created_at, kind";

    private readonly string prefix = $"int{Guid.NewGuid():N}:";

    private string Key => prefix + RedisWalletReadCache.Key;

    [Fact]
    public async Task PurchaseAndCancelInvalidateTheCacheBeforeWalletUpdatedAndRestRecoversTheBalance()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Settings()).WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.ConfigureTestServices(services =>
            {
                services.AddSingleton<RedisWalletReadCache>();
                services.AddSingleton<IWalletReadCache>(provider => new SlowInvalidation(provider.GetRequiredService<RedisWalletReadCache>()));
            });
        });
        using var http = factory.CreateClient();
        await using var client = await RealtimeTestClient.ConnectAsync(factory);

        Assert.IsType<SlowInvalidation>(factory.Services.GetService<IWalletReadCache>());
        Assert.NotNull(factory.Services.GetService<IHubContext<LaundryHub>>());
        Assert.Equal(5000L, await BalanceAsync(http));
        Assert.Equal(5000L, await CachedBalanceAsync());

        // Os avisos são lidos enquanto o comando ainda responde: a resposta HTTP não serve de barreira para a invalidação.
        var purchasing = PostBookingAsync(http);
        var bought = await ReceiveThroughWalletUpdatedAsync(client);
        using var purchase = await purchasing;
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);
        var created = await BookingApi.JsonAsync(purchase);
        var bookingId = created.GetProperty("bookingId").GetGuid();
        var itemId = created.GetProperty("items")[0].GetProperty("itemId").GetGuid();
        Assert.Equal(3110L, created.GetProperty("remainingBalanceCents").GetInt64());

        Assert.Equal(["machine.offered", "queue.updated", "booking.updated", "wallet.updated"], bought.Names);
        Assert.Equal((itemId, bookingId, 1L), (
            bought.Events[0].Payload.GetProperty("itemId").GetGuid(),
            bought.Events[2].Payload.GetProperty("bookingId").GetGuid(),
            bought.Events[2].Payload.GetProperty("version").GetInt64()));
        Assert.False(bought.KeyPresentAtWalletUpdated, "a projeção antiga ainda estava no Redis quando wallet.updated chegou");
        Assert.False(await redis.Database.KeyExistsAsync(Key));
        Assert.Equal(3110L, await BalanceAsync(http));
        Assert.Equal(3110L, await CachedBalanceAsync());

        var cancelling = http.PostAsync($"/api/v1/bookings/{bookingId}/items/{itemId}/cancel", null);
        var cancelled = await ReceiveThroughWalletUpdatedAsync(client);
        using var cancel = await cancelling;
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(1890L, (await BookingApi.JsonAsync(cancel)).GetProperty("refundedAmountCents").GetInt64());

        Assert.Equal(["booking.updated", "wallet.updated"], cancelled.Names);
        Assert.Equal((bookingId, 2L), (
            cancelled.Events[0].Payload.GetProperty("bookingId").GetGuid(),
            cancelled.Events[0].Payload.GetProperty("version").GetInt64()));
        Assert.False(cancelled.KeyPresentAtWalletUpdated, "a projeção antiga ainda estava no Redis quando wallet.updated chegou");
        Assert.False(await redis.Database.KeyExistsAsync(Key));
        Assert.Equal(5000L, await BalanceAsync(http));
        Assert.Equal(5000L, await CachedBalanceAsync());
        Assert.Equal(["debit|18.90|31.10", "refund|18.90|50.00"], await database.RowsAsync(LedgerSql));
        Assert.Equal("cancelled|2", await database.ScalarAsync<string>(
            $"SELECT i.status || '|' || r.revision FROM service_request_items i JOIN service_requests r ON r.id = i.request_id WHERE i.id = '{itemId}'"));
    }

    /// <summary>Avisos até `wallet.updated`, inclusive; no instante em que ele chega, consulta se a chave ainda existe.</summary>
    private async Task<(List<(string Name, JsonElement Payload)> Events, List<string> Names, bool KeyPresentAtWalletUpdated)> ReceiveThroughWalletUpdatedAsync(
        RealtimeTestClient client)
    {
        var events = new List<(string Name, JsonElement Payload)>();
        while (true)
        {
            var next = await client.NextAsync();
            events.Add(next);
            if (next.Name == "wallet.updated")
            {
                var present = await redis.Database.KeyExistsAsync(Key);
                return (events, [.. events.Select(received => received.Name)], present);
            }
        }
    }

    private Dictionary<string, string> Settings() => new()
    {
        ["Laundry:DemoMode"] = "true",
        ["Laundry:QueueWorkerIntervalSeconds"] = "3600",
        ["Realtime:Enabled"] = "true",
        ["Caching:Enabled"] = "true",
        ["Caching:Redis"] = redis.ConnectionString,
        ["Caching:KeyPrefix"] = prefix,
        ["Caching:TimeToLiveSeconds"] = "30",
        ["RateLimiting:Reads:PermitLimit"] = "1000",
        ["RateLimiting:BookingCreate:PermitLimit"] = "1000",
        ["RateLimiting:BookingReads:PermitLimit"] = "1000",
        ["RateLimiting:ItemCommands:PermitLimit"] = "1000",
        ["RateLimiting:ItemCancel:PermitLimit"] = "1000",
    };

    private async Task<long> CachedBalanceAsync()
    {
        using var stored = JsonDocument.Parse((string)(await redis.Database.StringGetAsync(Key))!);
        return stored.RootElement.GetProperty("balanceCents").GetInt64();
    }

    private static async Task<long> BalanceAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/wallet");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await BookingApi.JsonAsync(response)).GetProperty("balanceCents").GetInt64();
    }

    private static async Task<HttpResponseMessage> PostBookingAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings") { Content = new StringContent(WashBody, Encoding.UTF8, "application/json") };
        request.Headers.Add(CreateBookingEndpoint.IdempotencyKeyHeader, Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    /// <summary>Adaptador Redis real com a remoção atrasada: torna observável um aviso enviado antes da invalidação.</summary>
    private sealed class SlowInvalidation(IWalletReadCache inner) : IWalletReadCache
    {
        public Task<WalletBalance?> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);

        public Task<bool> WriteAsync(WalletBalance balance, CancellationToken cancellationToken) => inner.WriteAsync(balance, cancellationToken);

        public async Task<bool> InvalidateAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            return await inner.InvalidateAsync(cancellationToken);
        }
    }
}
