using System.Diagnostics.Metrics;
using System.Globalization;
using Cicclo.Api.Bookings;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Integration.Bookings;

/// <summary>
/// Composição da compra com a ativação inicial na mesma transação (QUEUE-01..03/05, FIN-08, OPS-03/11) e dívidas da
/// E02 (Z1: wallet demo ausente; Z2: falha de serialização 40001 real), contra PostgreSQL 18.1 real.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E04")]
[Trait("Task", "T2")]
public sealed class CreateBookingActivationTests(PostgresFixture postgres)
{
    private const string BookingApp = "cicclo-e04-booking";

    private const string ItemsSql =
        "SELECT service_code || '|' || status || '|' || coalesce(queue_ticket::text, 'null') || '|' || coalesce(machine_id::text, 'null') FROM service_request_items ORDER BY service_code COLLATE \"C\", id";

    [Fact]
    public async Task B01_SingleItemPurchaseOffersTheFreeMachineInTheSameCommitAsTheDebit()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();

        var result = await BookingApi.UseCase(Real(database), clock).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
        var item = Assert.Single(result.Booking!.Items);
        Assert.Equal(BookingItemStatus.ToBeConfirmed, item.Status);
        Assert.Equal(
            [
                $"item|{item.ItemId}|to_be_confirmed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|null",
                $"machine|washer-01|reserved|{item.ItemId}|12:00:00.000000",
                $"machine|dryer-01|available|null|{await UpdatedAtAsync(database, DryerId)}",
                "wallet|31.10|1",
            ],
            await database.RowsAsync(StateSql));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM wallet_transactions WHERE kind = 'debit' AND amount = 18.90 AND balance_after = 31.10"));
    }

    [Fact]
    public async Task B02_BatchWithPreferenceActivatesOneMatchingOccurrenceAndKeepsTheRestPending()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");

        var result = await BookingApi.UseCase(Real(database), Clock()).ExecuteAsync(Guid.NewGuid(), Intent(["wash", "dry", "wash"], 5870, "wash"), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
        // As duas lavagens têm a mesma ordem canônica; só uma delas é ativada.
        Assert.Equal(
            ["dry|PendingActivation", "wash|PendingActivation", "wash|ToBeConfirmed"],
            result.Booking!.Items.Select(item => $"{item.ServiceCode}|{item.Status}").Order(StringComparer.Ordinal));
        Assert.Equal(
            ["dry|pending_activation|null|null", "wash|pending_activation|null|null", $"wash|to_be_confirmed|1|{WasherId}"],
            (await database.RowsAsync(ItemsSql)).Order(StringComparer.Ordinal));
        Assert.Equal(["washer-01|reserved", "dryer-01|available"], await database.RowsAsync("SELECT name || '|' || state FROM machines ORDER BY id"));
        Assert.Equal("1|True", (await database.RowsAsync("SELECT last_value, is_called FROM queue_ticket_seq")).Single());
    }

    [Theory]
    [InlineData(new[] { "wash", "dry" }, 3980L)]
    [InlineData(new[] { "wash", "wash" }, 3780L)]
    public async Task B03_BatchWithoutPreferenceStaysPendingWithoutOccupyingMachinesOrTickets(string[] codes, long total)
    {
        var database = await ReadyAsync(postgres);

        var result = await BookingApi.UseCase(Real(database), Clock()).ExecuteAsync(Guid.NewGuid(), Intent(codes, total), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
        Assert.All(result.Booking!.Items, item => Assert.Equal(BookingItemStatus.PendingActivation, item.Status));
        Assert.All(await database.RowsAsync(ItemsSql), row => Assert.EndsWith("|pending_activation|null|null", row, StringComparison.Ordinal));
        Assert.Equal(["washer-01|available", "dryer-01|available"], await database.RowsAsync("SELECT name || '|' || state FROM machines ORDER BY id"));
        Assert.Equal("1|False", (await database.RowsAsync("SELECT last_value, is_called FROM queue_ticket_seq")).Single());
    }

    [Fact]
    public async Task B04_PurchaseOfABusyTypeQueuesBehindEarlierItemsWithANewTicket()
    {
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{WasherId}'");
        var clock = Clock();
        var first = await BookingApi.UseCase(Real(database), clock).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(3));

        var second = await BookingApi.UseCase(Real(database), clock).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None);

        Assert.Equal(BookingItemStatus.Queued, first.Booking!.Items[0].Status);
        Assert.Equal(BookingItemStatus.Queued, second.Booking!.Items[0].Status);
        Assert.Equal(
            [$"{first.Booking.Items[0].ItemId}|queued|1|12:00:00", $"{second.Booking.Items[0].ItemId}|queued|2|12:00:03"],
            await database.RowsAsync("SELECT id || '|' || status || '|' || queue_ticket || '|' || to_char(queued_at AT TIME ZONE 'UTC', 'HH24:MI:SS') FROM service_request_items ORDER BY queue_ticket"));
        Assert.Equal("12.20", await database.ScalarAsync<string>("SELECT balance::text FROM wallets"));
    }

    [Theory]
    [InlineData("machines", "NEW.state = 'reserved'")]
    [InlineData("service_request_items", "NEW.status = 'queued'")]
    public async Task B05_FailureWhileActivatingRevertsTheWholePurchaseAndKeepsTheKeyUsable(string table, string when)
    {
        var database = await ReadyAsync(postgres);
        await database.ExecuteAsync(
            $"""
            CREATE FUNCTION fail_activation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'ativação recusada'; END $$;
            CREATE TRIGGER fail_activation BEFORE UPDATE ON {table} FOR EACH ROW WHEN ({when}) EXECUTE FUNCTION fail_activation();
            """);
        var before = await database.RowsAsync(StateSql);
        var key = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            BookingApi.UseCase(Real(database), Clock()).ExecuteAsync(key, Intent(["wash"], 1890), CancellationToken.None));

        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Equal("0|0|0", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM service_request_items), (SELECT count(*) FROM wallet_transactions)")).Single());
        await database.ExecuteAsync($"DROP TRIGGER fail_activation ON {table}");
        var retried = await BookingApi.UseCase(Real(database), Clock()).ExecuteAsync(key, Intent(["wash"], 1890), CancellationToken.None);
        Assert.Equal(CreateBookingOutcome.Created, retried.Outcome);
    }

    [Fact]
    public async Task B06_PurchaseAfterADeadlineRequeuesTheExpiredOfferFirstAndRecordsQueueMetricsAfterCommit()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var earlier = await BookingApi.UseCase(Real(database), clock).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None);
        clock.SetUtcNow(T0.AddSeconds(61));
        using var metrics = new MetricCapture();

        var latecomer = await BookingApi.UseCase(Real(database), clock, metrics.Meters).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None);

        var a = earlier.Booking!.Items[0].ItemId;
        var b = latecomer.Booking!.Items[0].ItemId;
        Assert.Equal(BookingItemStatus.Queued, latecomer.Booking.Items[0].Status);
        Assert.Equal(
            [$"item|{a}|to_be_confirmed|2|12:01:01.000000|{WasherId}|12:01:01.000000|12:02:01.000000|null", $"item|{b}|queued|3|12:01:01.000000|null|null|null|null"],
            (await database.RowsAsync(StateSql)).Where(row => row.StartsWith("item|", StringComparison.Ordinal)).OrderBy(row => row.Contains("|queued|", StringComparison.Ordinal)));
        Assert.Equal(["cicclo_offers_expired_total|1|machine_type=washer", "cicclo_queue_wait_seconds|0|machine_type=washer"], metrics.Measurements.Order(StringComparer.Ordinal));
    }

    // Z1 (E02): a wallet demo ausente é base não preparada, nunca saldo zero nem recusa de saldo.
    [Fact]
    public async Task B07_MissingDemoWalletIsAnUnpreparedDatabaseNotAnInsufficientBalance()
    {
        var database = await ReadyAsync(postgres);
        await database.ExecuteAsync("DELETE FROM wallets");

        await Assert.ThrowsAsync<DatabaseUnavailableException>(() =>
            BookingApi.UseCase(Real(database), Clock()).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None));

        Assert.Equal("0|0|0", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM service_request_items), (SELECT count(*) FROM wallet_transactions)")).Single());
    }

    // Z2 (E02): uma falha de serialização 40001 emitida pelo PostgreSQL na transação da compra é rollback conhecido; a
    // unidade inteira é repetida em sessão nova e cobra uma única vez.
    [Fact]
    public async Task B08_RealSerializationFailureIsRetriedAsAWholeUnitAndChargesOnce()
    {
        var database = await ReadyAsync(postgres);
        await database.ExecuteAsync(
            """
            CREATE SEQUENCE z2_attempts;
            CREATE FUNCTION fail_once() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF nextval('z2_attempts') = 1 THEN
                    RAISE EXCEPTION 'serialização simulada' USING ERRCODE = 'serialization_failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER fail_once BEFORE INSERT ON wallet_transactions FOR EACH ROW EXECUTE FUNCTION fail_once();
            """);
        var sessions = new ScriptedBookingSessions(Real(database));

        var result = await BookingApi.UseCase(sessions, Clock()).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
        Assert.Equal(2, sessions.Opened);
        Assert.Equal(PostgresErrorCodes.SerializationFailure, Assert.IsType<PostgresException>(FindPostgres(Assert.Single(sessions.Failures))).SqlState);
        Assert.Equal("1|1|1|31.10|to_be_confirmed", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM service_request_items), (SELECT count(*) FROM wallet_transactions), (SELECT balance::text FROM wallets), (SELECT status FROM service_request_items)")).Single());
    }

    private static PostgresCreateBookingSessions Real(TestDatabase database) => new(database.ContextFactory(BookingApp));

    private static BookingIntent Intent(string[] codes, long expectedTotalCents, string? preferred = null) =>
        BookingIntent.Create(codes, preferred, expectedTotalCents, 10).Intent!;

    private static async Task<string> UpdatedAtAsync(TestDatabase database, string machineId) =>
        await database.ScalarAsync<string>($"SELECT to_char(updated_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') FROM machines WHERE id = '{machineId}'");

    private static PostgresException? FindPostgres(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    /// <summary>Medições das métricas de lavanderia criadas por uma fábrica de meters exclusiva do teste.</summary>
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
                if (ReferenceEquals(instrument.Meter.Scope, Meters))
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
