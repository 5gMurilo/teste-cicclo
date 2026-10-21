using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Cicclo.Api.Laundry;
using Cicclo.Api.Laundry.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cicclo.Api.Tests.Contracts.Notifications;

/// <summary>
/// Contratos wire dos avisos E09 (RT-04/09/10): um cliente SignalR 10.0.13 real, conectado por HTTP a um host de teste
/// isolado que registra o emissor de produção (AddLaundryNotifications) e mapeia o LaundryHub somente aqui, recebe os
/// nomes com ponto e os payloads por valor. O host da API não mapeia o Hub nesta tarefa.
/// </summary>
[Trait("Increment", "E09")]
[Trait("Task", "T2")]
public sealed class LaundryNotificationContractTests
{
    private static readonly DateTime OccurredAt = new(2026, 10, 21, 17, 30, 15, 123, DateTimeKind.Utc);
    private static readonly DateTime ExpiresAt = new(2026, 10, 21, 17, 31, 15, 123, DateTimeKind.Utc);
    private const string OccurredAtWire = "2026-10-21T17:30:15.123Z";
    private const string ExpiresAtWire = "2026-10-21T17:31:15.123Z";

    [Fact]
    public async Task N01_BookingUpdatedCarriesNewEventIdBookingIdConfirmedVersionAndUtc()
    {
        await using var host = await ProtocolHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var bookingId = Guid.NewGuid();
        var first = LaundryNotification.BookingUpdated(bookingId, 3, OccurredAt);
        var second = LaundryNotification.BookingUpdated(bookingId, 1, OccurredAt);

        await host.Publisher.PublishAsync([first, second], CancellationToken.None);

        var (name, payload) = await client.NextAsync();
        Assert.Equal("booking.updated", name);
        Assert.Equal(["bookingId", "eventId", "occurredAt", "version"], PropertyNames(payload));
        var eventId = Guid.Parse(payload.GetProperty("eventId").GetString()!);
        Assert.Equal(((BookingUpdatedPayload)first.Payload).EventId, eventId);
        Assert.Equal(7, eventId.Version);
        Assert.Equal(bookingId.ToString(), payload.GetProperty("bookingId").GetString());
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("version").ValueKind);
        Assert.Equal(3L, payload.GetProperty("version").GetInt64());
        Assert.Equal(OccurredAtWire, payload.GetProperty("occurredAt").GetString());
        var (secondName, secondPayload) = await client.NextAsync();
        Assert.Equal("booking.updated", secondName);
        Assert.Equal(1L, secondPayload.GetProperty("version").GetInt64());
        Assert.NotEqual(eventId, Guid.Parse(secondPayload.GetProperty("eventId").GetString()!));
    }

    [Theory]
    [InlineData(MachineType.Washer, "washer")]
    [InlineData(MachineType.Dryer, "dryer")]
    public async Task N02_QueueUpdatedCarriesMachineTypeContractAndUtc(MachineType machineType, string wireType)
    {
        await using var host = await ProtocolHost.StartAsync();
        await using var client = await host.ConnectAsync();

        await host.Publisher.PublishAsync([LaundryNotification.QueueUpdated(machineType, OccurredAt)], CancellationToken.None);

        var (name, payload) = await client.NextAsync();
        Assert.Equal("queue.updated", name);
        Assert.Equal(["machineType", "occurredAt"], PropertyNames(payload));
        Assert.Equal(wireType, payload.GetProperty("machineType").GetString());
        Assert.Equal(OccurredAtWire, payload.GetProperty("occurredAt").GetString());
    }

    [Fact]
    public async Task N03_MachineOfferedCarriesItemMachineDeadlineAndUtc()
    {
        await using var host = await ProtocolHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var (itemId, machineId) = (Guid.NewGuid(), Guid.NewGuid());

        await host.Publisher.PublishAsync([LaundryNotification.MachineOffered(itemId, machineId, ExpiresAt, OccurredAt)], CancellationToken.None);

        var (name, payload) = await client.NextAsync();
        Assert.Equal("machine.offered", name);
        Assert.Equal(["expiresAt", "itemId", "machineId", "occurredAt"], PropertyNames(payload));
        Assert.Equal(itemId.ToString(), payload.GetProperty("itemId").GetString());
        Assert.Equal(machineId.ToString(), payload.GetProperty("machineId").GetString());
        Assert.Equal(ExpiresAtWire, payload.GetProperty("expiresAt").GetString());
        Assert.Equal(OccurredAtWire, payload.GetProperty("occurredAt").GetString());
    }

    [Fact]
    public async Task N04_MachineOfferExpiredCarriesItemAndUtc()
    {
        await using var host = await ProtocolHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var itemId = Guid.NewGuid();

        await host.Publisher.PublishAsync([LaundryNotification.MachineOfferExpired(itemId, OccurredAt)], CancellationToken.None);

        var (name, payload) = await client.NextAsync();
        Assert.Equal("machine.offer_expired", name);
        Assert.Equal(["itemId", "occurredAt"], PropertyNames(payload));
        Assert.Equal(itemId.ToString(), payload.GetProperty("itemId").GetString());
        Assert.Equal(OccurredAtWire, payload.GetProperty("occurredAt").GetString());
    }

    [Fact]
    public async Task N05_WalletUpdatedCarriesOnlyUtcWithoutBalance()
    {
        await using var host = await ProtocolHost.StartAsync();
        await using var client = await host.ConnectAsync();

        await host.Publisher.PublishAsync([LaundryNotification.WalletUpdated(OccurredAt)], CancellationToken.None);

        var (name, payload) = await client.NextAsync();
        Assert.Equal("wallet.updated", name);
        Assert.Equal(["occurredAt"], PropertyNames(payload));
        Assert.Equal(OccurredAtWire, payload.GetProperty("occurredAt").GetString());
    }

    [Fact]
    public async Task N06_MachineFailedCarriesMachineAndUtc()
    {
        await using var host = await ProtocolHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var machineId = Guid.NewGuid();

        await host.Publisher.PublishAsync([LaundryNotification.MachineFailed(machineId, OccurredAt)], CancellationToken.None);

        var (name, payload) = await client.NextAsync();
        Assert.Equal("machine.failed", name);
        Assert.Equal(["machineId", "occurredAt"], PropertyNames(payload));
        Assert.Equal(machineId.ToString(), payload.GetProperty("machineId").GetString());
        Assert.Equal(OccurredAtWire, payload.GetProperty("occurredAt").GetString());
    }

    [Fact]
    public async Task N07_RealClientReceivesEveryEventInPublishOrderAndHubAcceptsNoCommands()
    {
        await using var host = await ProtocolHost.StartAsync();
        await using var client = await host.ConnectAsync();
        Guid itemId = Guid.NewGuid(), machineId = Guid.NewGuid();

        await host.Publisher.PublishAsync(
            [
                LaundryNotification.WalletUpdated(OccurredAt),
                LaundryNotification.BookingUpdated(Guid.NewGuid(), 2, OccurredAt),
                LaundryNotification.MachineOfferExpired(itemId, OccurredAt),
                LaundryNotification.QueueUpdated(MachineType.Washer, OccurredAt),
                LaundryNotification.MachineOffered(itemId, machineId, ExpiresAt, OccurredAt),
                LaundryNotification.MachineFailed(machineId, OccurredAt),
            ],
            CancellationToken.None);

        var received = new List<string>();
        for (var index = 0; index < 6; index++)
        {
            received.Add((await client.NextAsync()).Name);
        }

        Assert.Equal(
            ["wallet.updated", "booking.updated", "machine.offer_expired", "queue.updated", "machine.offered", "machine.failed"], received);
        Assert.Empty(typeof(LaundryHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
        foreach (var command in new[] { "CreateBooking", "ConfirmItem", "CancelItem", "Refund", "SelectWallet" })
        {
            var error = await Assert.ThrowsAsync<HubException>(() => client.Connection.InvokeAsync<object>(command, Guid.NewGuid()));
            Assert.Contains(command, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task N08_PublisherNeedsOnlyHubContextAndSurfacesTheFirstTransportFailureToTheCaller()
    {
        await using var host = await ProtocolHost.StartAsync();
        var constructor = Assert.Single(typeof(LaundryNotificationPublisher).GetConstructors());
        Assert.Equal([typeof(IHubContext<LaundryHub>)], constructor.GetParameters().Select(parameter => parameter.ParameterType));
        using (var scope = host.Services.CreateScope())
        {
            Assert.Same(host.Publisher, scope.ServiceProvider.GetRequiredService<ILaundryNotifications>());
        }

        Assert.IsType<LaundryNotificationPublisher>(host.Publisher);
        Assert.IsType<DefaultHubLifetimeManager<LaundryHub>>(host.Services.GetRequiredService<HubLifetimeManager<LaundryHub>>());
        var failure = new IOException("transporte indisponível");
        var hub = new FailingHubContext(failAt: 2, failure);
        var publisher = new LaundryNotificationPublisher(hub);

        var error = await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(
            [
                LaundryNotification.WalletUpdated(OccurredAt),
                LaundryNotification.BookingUpdated(Guid.NewGuid(), 1, OccurredAt),
                LaundryNotification.QueueUpdated(MachineType.Dryer, OccurredAt),
            ],
            CancellationToken.None));

        Assert.Same(failure, error);
        Assert.Equal(["wallet.updated", "booking.updated"], hub.Attempts);
    }

    [Fact]
    public void N09_FactoriesRejectNonUtcDatesEmptyIdsAndVersionsBelowTheInitialRevision()
    {
        var local = DateTime.SpecifyKind(OccurredAt, DateTimeKind.Local);
        var unspecified = DateTime.SpecifyKind(OccurredAt, DateTimeKind.Unspecified);

        Assert.Equal("version", Assert.Throws<ArgumentOutOfRangeException>(() => LaundryNotification.BookingUpdated(Guid.NewGuid(), 0, OccurredAt)).ParamName);
        Assert.Equal("bookingId", Assert.Throws<ArgumentException>(() => LaundryNotification.BookingUpdated(Guid.Empty, 1, OccurredAt)).ParamName);
        Assert.Equal("occurredAt", Assert.Throws<ArgumentException>(() => LaundryNotification.BookingUpdated(Guid.NewGuid(), 1, local)).ParamName);
        Assert.Equal("occurredAt", Assert.Throws<ArgumentException>(() => LaundryNotification.QueueUpdated(MachineType.Washer, unspecified)).ParamName);
        Assert.Equal("expiresAt", Assert.Throws<ArgumentException>(() => LaundryNotification.MachineOffered(Guid.NewGuid(), Guid.NewGuid(), local, OccurredAt)).ParamName);
        Assert.Equal("machineId", Assert.Throws<ArgumentException>(() => LaundryNotification.MachineOffered(Guid.NewGuid(), Guid.Empty, ExpiresAt, OccurredAt)).ParamName);
        Assert.Equal("itemId", Assert.Throws<ArgumentException>(() => LaundryNotification.MachineOfferExpired(Guid.Empty, OccurredAt)).ParamName);
        Assert.Equal("occurredAt", Assert.Throws<ArgumentException>(() => LaundryNotification.WalletUpdated(unspecified)).ParamName);
        Assert.Equal("machineId", Assert.Throws<ArgumentException>(() => LaundryNotification.MachineFailed(Guid.Empty, OccurredAt)).ParamName);
        Assert.Equal(1L, ((BookingUpdatedPayload)LaundryNotification.BookingUpdated(Guid.NewGuid(), 1, OccurredAt).Payload).Version);
    }

    private static string[] PropertyNames(JsonElement payload) =>
        payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Host de teste mínimo: o registro de produção do emissor e o Hub mapeado apenas aqui, sem banco.</summary>
    private sealed class ProtocolHost : IAsyncDisposable
    {
        private readonly WebApplication app;

        private ProtocolHost(WebApplication app) => this.app = app;

        public IServiceProvider Services => app.Services;

        public ILaundryNotifications Publisher => app.Services.GetRequiredService<ILaundryNotifications>();

        public static async Task<ProtocolHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddLaundryNotifications();
            var app = builder.Build();
            app.MapHub<LaundryHub>(LaundryHub.Path);
            await app.StartAsync();
            return new ProtocolHost(app);
        }

        /// <summary>
        /// Conecta por long polling sobre o TestServer. A primeira invocação recusada funciona como barreira: o servidor
        /// só despacha invocações depois de registrar a conexão no lifetime manager, então Clients.All já a inclui.
        /// </summary>
        public async Task<ProtocolClient> ConnectAsync()
        {
            var server = app.GetTestServer();
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(server.BaseAddress, LaundryHub.Path), options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                })
                .Build();
            var client = new ProtocolClient(connection);
            await connection.StartAsync();
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<object>("Ready"));
            return client;
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    private sealed class ProtocolClient : IAsyncDisposable
    {
        /// <summary>Nomes da tabela de eventos do design, literais: o cliente não herda as constantes do servidor.</summary>
        private static readonly string[] EventNames =
            ["booking.updated", "queue.updated", "machine.offered", "machine.offer_expired", "wallet.updated", "machine.failed"];

        private readonly Channel<(string Name, JsonElement Payload)> received = Channel.CreateUnbounded<(string, JsonElement)>();

        public ProtocolClient(HubConnection connection)
        {
            Connection = connection;
            foreach (var name in EventNames)
            {
                connection.On<JsonElement>(name, payload => received.Writer.TryWrite((name, payload)));
            }
        }

        public HubConnection Connection { get; }

        public async Task<(string Name, JsonElement Payload)> NextAsync() =>
            await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }

    /// <summary>Transporte que falha na N-ésima tentativa de envio, registrando os nomes tentados até ali.</summary>
    private sealed class FailingHubContext(int failAt, Exception failure) : IHubContext<LaundryHub>, IHubClients, IClientProxy
    {
        public List<string> Attempts { get; } = [];

        IHubClients IHubContext<LaundryHub>.Clients => this;

        IGroupManager IHubContext<LaundryHub>.Groups => throw new NotSupportedException();

        public IClientProxy All => this;

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Attempts.Add(method);
            return Attempts.Count == failAt ? Task.FromException(failure) : Task.CompletedTask;
        }

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Client(string connectionId) => throw new NotSupportedException();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IClientProxy Group(string groupName) => throw new NotSupportedException();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public IClientProxy User(string userId) => throw new NotSupportedException();

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }
}
