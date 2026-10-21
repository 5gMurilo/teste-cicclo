using System.Diagnostics.Metrics;
using System.Globalization;
using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry;
using Cicclo.Api.Laundry.ConfirmOffer;
using Cicclo.Api.Laundry.ReconcileOffers;
using Cicclo.Api.Tests.Support;
using Cicclo.Api.Wallet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;
using ConfirmOfferUseCase = Cicclo.Api.Laundry.ConfirmOffer.ConfirmOffer;
using ReconcileOffersUseCase = Cicclo.Api.Laundry.ReconcileOffers.ReconcileOffers;

namespace Cicclo.Api.Tests.Integration.Laundry;

/// <summary>
/// Expiração e reentrada no fim da fila (QUEUE-05/06/09..11, OPS-11) contra PostgreSQL 18.1 real com FakeTimeProvider:
/// a oferta vencida perde máquina e prazo, ganha ticket e queued_at novos, o próximo elegível recebe a máquina, nada é
/// cobrado ou cancelado, e o resultado Expired só existe depois do commit da reconciliação.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E03")]
[Trait("Task", "T4")]
public sealed class ReconcileOffersTests(PostgresFixture postgres)
{
    private const string ReconcileApp = "cicclo-e03-reconcile";
    private const string ConfirmApp = "cicclo-e03-confirm";
    private const string HolderApp = "cicclo-e03-holder";
    private const string SetupApp = "cicclo-e03-setup";

    [Fact]
    public async Task D01_ExpiredOfferGoesToTheEndWithAGreaterTicketAndTheNextQueuedTakesTheMachineWithoutMoney()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (a, b, c) = await OfferAndQueueAsync(database, clock);
        clock.SetUtcNow(T0.AddSeconds(60));
        using var metrics = new MetricCapture();

        var changes = await UseCase(database, clock, metrics.Meters).ExecuteAsync(CancellationToken.None);

        var washer = changes.Single(change => change.Type == MachineType.Washer);
        Assert.Equal([new ExpiredOffer(a, Guid.Parse(WasherId), T0.UtcDateTime.AddSeconds(60), 4, T0.UtcDateTime.AddSeconds(60))], washer.Expired);
        Assert.Equal([new MachineOffer(b, Guid.Parse(WasherId), T0.UtcDateTime.AddSeconds(1), T0.UtcDateTime.AddSeconds(60), T0.UtcDateTime.AddSeconds(120))], washer.Offers);
        Assert.Equal(new QueueChanges(MachineType.Dryer, [], []).Expired, changes.Single(change => change.Type == MachineType.Dryer).Expired);
        Assert.Equal(
            [
                $"{a}|queued|4|12:01:00.000000|null|null|null|null",
                $"{b}|to_be_confirmed|2|12:00:01.000000|{WasherId}|12:01:00.000000|12:02:00.000000|null",
                $"{c}|queued|3|12:00:02.000000|null|null|null|null",
                $"washer-01|reserved|{b}|12:01:00.000000",
                "wallet|50.00|0",
            ],
            await RowsAsync(database, a, b, c));
        Assert.Equal(
            ["cicclo_offers_expired_total|1|machine_type=washer", "cicclo_queue_wait_seconds|59|machine_type=washer"],
            metrics.Measurements.Order(StringComparer.Ordinal));
        // Posições derivadas depois da reentrada: C (ticket 3) à frente de A (ticket 4).
        Assert.Equal(1, (await ActivateAsync(Sessions(database, SetupApp), Coordinator(clock), c)).Item!.QueuePosition);
        Assert.Equal(2, (await ActivateAsync(Sessions(database, SetupApp), Coordinator(clock), a)).Item!.QueuePosition);
    }

    [Fact]
    public async Task D02_AnExpiredItemAloneInTheQueueMayReceiveANewSixtySecondOffer()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var a = await PendingAsync(database);
        await ActivateAsync(Sessions(database, SetupApp), Coordinator(clock), a);
        clock.SetUtcNow(T0.AddSeconds(75));

        var washer = (await UseCase(database, clock).ExecuteAsync(CancellationToken.None)).Single(change => change.Type == MachineType.Washer);

        Assert.Equal([a], washer.Expired.Select(expired => expired.ItemId));
        Assert.Equal([new MachineOffer(a, Guid.Parse(WasherId), T0.UtcDateTime.AddSeconds(75), T0.UtcDateTime.AddSeconds(75), T0.UtcDateTime.AddSeconds(135))], washer.Offers);
        Assert.Equal(
            [$"{a}|to_be_confirmed|2|12:01:15.000000|{WasherId}|12:01:15.000000|12:02:15.000000|null", $"washer-01|reserved|{a}|12:01:15.000000", "wallet|50.00|0"],
            await RowsAsync(database, a));
    }

    [Theory]
    [InlineData(59_999_999L)]
    [InlineData(30_000_000L)]
    public async Task D03_AnOfferStillInsideItsDeadlineAndAnIdleQueueAreLeftUntouched(long elapsedMicroseconds)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await OfferAndQueueAsync(database, clock);
        var before = await database.RowsAsync(StateSql);
        var sequence = await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq");
        clock.SetUtcNow(T0.AddTicks(elapsedMicroseconds * 10));
        using var metrics = new MetricCapture();

        var changes = await UseCase(database, clock, metrics.Meters).ExecuteAsync(CancellationToken.None);

        Assert.All(changes, change => Assert.Empty(change.Expired));
        Assert.All(changes, change => Assert.Empty(change.Offers));
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Equal(sequence, await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq"));
        Assert.Empty(metrics.Measurements);
    }

    [Theory]
    [InlineData("confirm first")]
    [InlineData("reconcile first")]
    public async Task D04_ConfirmAndExpireOnTheSameOfferHaveASingleWinner(string order)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (a, b, _) = await OfferAndQueueAsync(database, clock);
        var booking = await database.ScalarAsync<Guid>($"SELECT request_id FROM service_request_items WHERE id = '{a}'");

        ConfirmOfferResult confirm;
        IReadOnlyList<QueueChanges> reconcile;
        if (order == "confirm first")
        {
            clock.SetUtcNow(T0.AddSeconds(59));
            confirm = await Confirm(database, clock).ExecuteAsync(booking, a, CancellationToken.None);
            clock.SetUtcNow(T0.AddSeconds(61));
            reconcile = await UseCase(database, clock).ExecuteAsync(CancellationToken.None);
        }
        else
        {
            clock.SetUtcNow(T0.AddSeconds(60));
            reconcile = await UseCase(database, clock).ExecuteAsync(CancellationToken.None);
            confirm = await Confirm(database, clock).ExecuteAsync(booking, a, CancellationToken.None);
        }

        var expirations = reconcile.Sum(change => change.Expired.Count);
        if (order == "confirm first")
        {
            Assert.Equal((ConfirmOfferOutcome.Started, 0), (confirm.Outcome, expirations));
            Assert.Equal($"in_progress|12:00:59|running|{a}", await ItemAndWasherAsync(database, a));
        }
        else
        {
            Assert.Equal((ConfirmOfferOutcome.InvalidState, 1), (confirm.Outcome, expirations));
            Assert.Equal($"queued|null|reserved|{b}", await ItemAndWasherAsync(database, a));
        }
    }

    [Fact]
    public async Task D05_ConcurrentConfirmAndReconcileAfterTheDeadlineRequeueTheOfferOnlyOnce()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (a, b, c) = await OfferAndQueueAsync(database, clock);
        var booking = await database.ScalarAsync<Guid>($"SELECT request_id FROM service_request_items WHERE id = '{a}'");
        clock.SetUtcNow(T0.AddSeconds(60));
        var (holder, transaction) = await HoldAsync(database, HolderApp, $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE");

        // A confirmação entra primeiro na fila do lock da wallet; o worker espera atrás dela.
        var confirm = Task.Run(() => Confirm(database, clock).ExecuteAsync(booking, a, CancellationToken.None));
        await WaitUntilWaitingOnLockAsync(database, ConfirmApp);
        var reconcile = Task.Run(() => UseCase(database, clock).ExecuteAsync(CancellationToken.None));
        await WaitUntilWaitingOnLockAsync(database, ReconcileApp);
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        var confirmed = await confirm.WaitAsync(SignalTimeout);
        var reconciled = await reconcile.WaitAsync(SignalTimeout);

        Assert.Equal(ConfirmOfferOutcome.Expired, confirmed.Outcome);
        Assert.Equal(0, reconciled.Sum(change => change.Expired.Count));
        Assert.Equal(
            [
                $"{a}|queued|4|12:01:00.000000|null|null|null|null",
                $"{b}|to_be_confirmed|2|12:00:01.000000|{WasherId}|12:01:00.000000|12:02:00.000000|null",
                $"{c}|queued|3|12:00:02.000000|null|null|null|null",
                $"washer-01|reserved|{b}|12:01:00.000000",
                "wallet|50.00|0",
            ],
            await RowsAsync(database, a, b, c));
        Assert.Equal(4L, await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq"));
    }

    [Fact]
    public async Task D06_ExpiredConfirmationCommitsTheRequeueBeforeAnsweringExpired()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (a, b, c) = await OfferAndQueueAsync(database, clock);
        var booking = await database.ScalarAsync<Guid>($"SELECT request_id FROM service_request_items WHERE id = '{a}'");
        clock.SetUtcNow(T0.AddSeconds(61));
        using var metrics = new MetricCapture();

        var result = await Confirm(database, clock, metrics.Meters).ExecuteAsync(booking, a, CancellationToken.None);

        Assert.Equal(ConfirmOfferOutcome.Expired, result.Outcome);
        Assert.Equal(new ItemPlacement(a, BookingItemStatus.Queued, null, null, 2), result.Item);
        Assert.Equal(
            [
                $"{a}|queued|4|12:01:01.000000|null|null|null|null",
                $"{b}|to_be_confirmed|2|12:00:01.000000|{WasherId}|12:01:01.000000|12:02:01.000000|null",
                $"{c}|queued|3|12:00:02.000000|null|null|null|null",
                $"washer-01|reserved|{b}|12:01:01.000000",
                "wallet|50.00|0",
            ],
            await RowsAsync(database, a, b, c));
        Assert.Equal(["cicclo_offers_expired_total|1|machine_type=washer", "cicclo_queue_wait_seconds|60|machine_type=washer"], metrics.Measurements.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("confirm")]
    public async Task D07_LosingTheConnectionAtCommitUndoesTheWholeReconciliationAndRecordsNoMetric(string caller)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (a, _, _) = await OfferAndQueueAsync(database, clock);
        var booking = await database.ScalarAsync<Guid>($"SELECT request_id FROM service_request_items WHERE id = '{a}'");
        var before = await database.RowsAsync(StateSql);
        clock.SetUtcNow(T0.AddSeconds(90));
        using var metrics = new MetricCapture();
        var app = caller == "worker" ? ReconcileApp : ConfirmApp;
        var sessions = new HookedLaundrySessions(Sessions(database, app), () => TerminateAsync(database, app));

        var error = await Record.ExceptionAsync(() => caller == "worker"
            ? new ReconcileOffersUseCase(sessions, Coordinator(clock), Metrics(metrics.Meters), NullLogger<ReconcileOffersUseCase>.Instance).ExecuteAsync(CancellationToken.None)
            : new ConfirmOfferUseCase(sessions, Coordinator(clock), Metrics(metrics.Meters), clock, NullLogger<ConfirmOfferUseCase>.Instance).ExecuteAsync(booking, a, CancellationToken.None));

        Assert.NotNull(error);
        Assert.Contains(Chain(error), exception => exception is NpgsqlException);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Contains($"item|{a}|to_be_confirmed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|null", before);
        Assert.Empty(metrics.Measurements);
    }

    [Fact]
    public async Task D08_ActivationAfterTheDeadlineReconcilesTheExpiredOfferBeforeCompetingForTheMachine()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var a = await PendingAsync(database);
        var latecomer = await PendingAsync(database);
        await ActivateAsync(Sessions(database, SetupApp), Coordinator(clock), a);
        clock.SetUtcNow(T0.AddSeconds(61));

        var result = await ActivateAsync(Sessions(database, SetupApp), Coordinator(clock), latecomer);

        Assert.Equal([new ExpiredOffer(a, Guid.Parse(WasherId), T0.UtcDateTime.AddSeconds(60), 2, T0.UtcDateTime.AddSeconds(61))], result.Expired);
        Assert.Equal([a], result.Offers.Select(offer => offer.ItemId));
        Assert.Equal(new ItemPlacement(latecomer, BookingItemStatus.Queued, null, null, 1), result.Item);
        Assert.Equal(
            [$"{a}|to_be_confirmed|2|12:01:01.000000|{WasherId}|12:01:01.000000|12:02:01.000000|null", $"{latecomer}|queued|3|12:01:01.000000|null|null|null|null", $"washer-01|reserved|{a}|12:01:01.000000", "wallet|50.00|0"],
            await RowsAsync(database, a, latecomer));
    }

    [Fact]
    public async Task D09_SeveralExpiredOffersOfBothTypesRequeueInTicketOrderAndFreeMachinesServeTheQueue()
    {
        var database = await ReadyAsync(postgres);
        await database.ExecuteAsync($"INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES ('{Washer2Id}', 'washer-02', 'washer', 'available', now())");
        var clock = Clock();
        var sessions = Sessions(database, SetupApp);
        var first = await PendingAsync(database);
        var second = await PendingAsync(database);
        var waiting = await PendingAsync(database);
        var dry = await PendingAsync(database, "dry");
        foreach (var item in new[] { first, second, waiting, dry })
        {
            await ActivateAsync(sessions, Coordinator(clock), item);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        clock.SetUtcNow(T0.AddMinutes(2));
        using var metrics = new MetricCapture();

        var changes = await UseCase(database, clock, metrics.Meters).ExecuteAsync(CancellationToken.None);

        Assert.Equal([MachineType.Dryer, MachineType.Washer], changes.Select(change => change.Type));
        var washer = changes[1];
        // A secadora é reconciliada antes (ordem de machine_type), então seu item recebe o ticket 5.
        Assert.Equal([(first, 6L), (second, 7L)], washer.Expired.Select(expired => (expired.ItemId, expired.NewTicket)));
        Assert.Equal([(waiting, Guid.Parse(WasherId)), (first, Guid.Parse(Washer2Id))], washer.Offers.Select(offer => (offer.ItemId, offer.MachineId)));
        Assert.Equal([(dry, 5L)], changes[0].Expired.Select(expired => (expired.ItemId, expired.NewTicket)));
        Assert.Equal([dry], changes[0].Offers.Select(offer => offer.ItemId));
        Assert.Equal(
            [$"{first}|to_be_confirmed|6", $"{second}|queued|7", $"{waiting}|to_be_confirmed|3", $"{dry}|to_be_confirmed|5"],
            await database.RowsAsync($"SELECT id || '|' || status || '|' || queue_ticket FROM service_request_items ORDER BY array_position(ARRAY['{first}', '{second}', '{waiting}', '{dry}']::uuid[], id)"));
        Assert.Equal(
            [
                "cicclo_offers_expired_total|1|machine_type=dryer",
                "cicclo_offers_expired_total|2|machine_type=washer",
                "cicclo_queue_wait_seconds|0|machine_type=dryer",
                "cicclo_queue_wait_seconds|0|machine_type=washer",
                "cicclo_queue_wait_seconds|118|machine_type=washer",
            ],
            metrics.Measurements.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task D10_ReconciliationLocksTheMachinesOfEveryTypeBeforeAnyItem()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var dry = await PendingAsync(database, "dry");
        await ActivateAsync(Sessions(database, SetupApp), Coordinator(clock), dry);
        var (holder, transaction) = await HoldAsync(database, HolderApp, $"SELECT 1 FROM machines WHERE id = '{WasherId}' FOR UPDATE");

        var reconcile = Task.Run(() => UseCase(database, clock).ExecuteAsync(CancellationToken.None));
        await WaitUntilWaitingOnLockAsync(database, ReconcileApp);

        Assert.False(await CanLockNowAsync(database, $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE"));
        Assert.False(await CanLockNowAsync(database, $"SELECT 1 FROM machines WHERE id = '{DryerId}' FOR UPDATE"));
        Assert.True(await CanLockNowAsync(database, $"SELECT 1 FROM service_request_items WHERE id = '{dry}' FOR UPDATE"));
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        await reconcile.WaitAsync(SignalTimeout);
    }

    [Theory]
    [InlineData("offer still valid")]
    [InlineData("machine not reserved for the item")]
    public async Task D11_RequeueRefusesAnOfferThatIsNotDueOrNotLinkedWithoutEffect(string scenario)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var a = await PendingAsync(database);
        await ActivateAsync(Sessions(database, SetupApp), Coordinator(clock), a);
        if (scenario != "offer still valid")
        {
            await database.ExecuteAsync($"UPDATE machines SET state = 'running', active_item_id = NULL, is_demo_occupied = true WHERE id = '{WasherId}'");
        }

        var before = await database.RowsAsync(StateSql);
        var at = scenario == "offer still valid" ? T0.UtcDateTime.AddSeconds(59) : T0.UtcDateTime.AddSeconds(61);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var session = await Sessions(database, ReconcileApp).OpenAsync(CancellationToken.None);
            await session.LockWalletAsync(DemoWallet.Id, CancellationToken.None);
            await session.RequeueExpiredAsync(a, Guid.Parse(WasherId), 99, at, CancellationToken.None);
            await session.CommitAsync();
        });

        Assert.StartsWith("Transição de fila recusada", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    /// <summary>
    /// Instante da decisão depois dos locks também na reconciliação (dívida N16 da E03, design D02): um detentor fora do
    /// protocolo segura a lavadora enquanto o relógio passa de 59 s para 61 s; a decisão usa 61 s, expira a oferta e
    /// reoferta o item sozinho por mais 60 s.
    /// </summary>
    [Fact]
    public async Task D12_ReconciliationTakesTheInstantAfterWaitingForTheMachineLock()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var a = await PendingAsync(database);
        await ActivateAsync(Sessions(database, SetupApp), Coordinator(clock), a);
        clock.SetUtcNow(T0.AddSeconds(59));
        var (holder, transaction) = await HoldAsync(database, HolderApp, $"SELECT 1 FROM machines WHERE id = '{WasherId}' FOR UPDATE");

        var reconcile = Task.Run(() => UseCase(database, clock).ExecuteAsync(CancellationToken.None));
        await WaitUntilWaitingOnLockAsync(database, ReconcileApp);
        Assert.False(reconcile.IsCompleted);
        clock.SetUtcNow(T0.AddSeconds(61));
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        var changes = await reconcile.WaitAsync(SignalTimeout);

        Assert.Equal([new ExpiredOffer(a, Guid.Parse(WasherId), T0.UtcDateTime.AddSeconds(60), 2, T0.UtcDateTime.AddSeconds(61))], changes.SelectMany(change => change.Expired));
        Assert.Equal(
            [$"{a}|to_be_confirmed|2|12:01:01.000000|{WasherId}|12:01:01.000000|12:02:01.000000|null", $"washer-01|reserved|{a}|12:01:01.000000", "wallet|50.00|0"],
            await RowsAsync(database, a));
    }

    private static ReconcileOffersUseCase UseCase(TestDatabase database, TimeProvider clock, IMeterFactory? meters = null) =>
        new(Sessions(database, ReconcileApp), Coordinator(clock), Metrics(meters), NullLogger<ReconcileOffersUseCase>.Instance);

    private static ConfirmOfferUseCase Confirm(TestDatabase database, TimeProvider clock, IMeterFactory? meters = null) =>
        new(Sessions(database, ConfirmApp), Coordinator(clock), Metrics(meters), clock, NullLogger<ConfirmOfferUseCase>.Instance);

    /// <summary>A ofertado na lavadora em T0 (ticket 1), B e C na fila em T0+1 s e T0+2 s (tickets 2 e 3).</summary>
    private static async Task<(Guid A, Guid B, Guid C)> OfferAndQueueAsync(TestDatabase database, FakeTimeProvider clock)
    {
        var sessions = Sessions(database, SetupApp);
        var items = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var item = await PendingAsync(database);
            clock.SetUtcNow(T0.AddSeconds(index));
            await ActivateAsync(sessions, Coordinator(clock), item);
            items.Add(item);
        }

        return (items[0], items[1], items[2]);
    }

    /// <summary>Linhas dos itens na ordem pedida, depois a lavadora washer-01 e a wallet (saldo e lançamentos).</summary>
    private static async Task<List<string>> RowsAsync(TestDatabase database, params Guid[] items)
    {
        var state = await database.RowsAsync(StateSql);
        return
        [
            .. items.Select(item => state.Single(row => row.StartsWith($"item|{item}|", StringComparison.Ordinal))["item|".Length..]),
            .. state.Where(row => row.StartsWith("machine|washer-01|", StringComparison.Ordinal)).Select(row => row["machine|".Length..]),
            state[^1],
        ];
    }

    private static async Task<string> ItemAndWasherAsync(TestDatabase database, Guid item) =>
        await database.ScalarAsync<string>(
            $"""
            SELECT i.status || '|' || coalesce(to_char(i.started_at AT TIME ZONE 'UTC', 'HH24:MI:SS'), 'null') || '|' || m.state || '|' || m.active_item_id
            FROM service_request_items i, machines m WHERE i.id = '{item}' AND m.id = '{WasherId}'
            """);

    private static async Task TerminateAsync(TestDatabase database, string applicationName) =>
        Assert.Equal(["true"], await database.RowsAsync(
            $"SELECT pg_terminate_backend(pid, {(int)SignalTimeout.TotalMilliseconds})::text FROM pg_stat_activity WHERE datname = current_database() AND application_name = '{applicationName}'"));

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            yield return exception;
        }
    }

    /// <summary>Captura as medições das métricas de lavanderia criadas por uma fábrica de meters exclusiva do teste.</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        private readonly MeterListener listener = new();
        private readonly List<string> measurements = [];

        public MetricCapture()
        {
            Meters = services.GetRequiredService<IMeterFactory>();
            listener.InstrumentPublished = (instrument, subscription) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, Meters) && instrument.Meter.Name == LaundryMetrics.MeterName)
                {
                    subscription.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            listener.Start();
        }

        public IMeterFactory Meters { get; }

        public List<string> Measurements
        {
            get
            {
                lock (measurements)
                {
                    return [.. measurements];
                }
            }
        }

        public void Dispose()
        {
            listener.Dispose();
            services.Dispose();
        }

        private void Add<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"{instrument.Name}|{value}|")
                + string.Join(',', tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}"));
            lock (measurements)
            {
                measurements.Add(line);
            }
        }
    }
}
