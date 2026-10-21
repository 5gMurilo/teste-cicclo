using System.Text;
using Cicclo.Api.Laundry;
using Cicclo.Api.Laundry.ConfirmOffer;
using Cicclo.Api.Platform.BackgroundServices;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;
using ConfirmOfferUseCase = Cicclo.Api.Laundry.ConfirmOffer.ConfirmOffer;

namespace Cicclo.Api.Tests.Integration.Laundry;

/// <summary>
/// Worker de reconciliação no host real (WebApplicationFactory) sobre PostgreSQL 18.1, com FakeTimeProvider no
/// lugar do relógio do sistema: QUEUE-12 (reinício reconcilia do banco, intervalo padrão 5 s), OPS-10 (falha logada
/// sem busy-spin, encerramento ordenado) e OPS-11 (métricas exportadas). Nenhuma espera real de 5 s ou 60 s.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E03")]
[Trait("Task", "T5")]
public sealed class OfferReconciliationWorkerTests(PostgresFixture postgres)
{
    private const string WorkerApp = "cicclo-e03-worker";
    private const string HolderApp = "cicclo-e03-holder";
    private const string SetupApp = "cicclo-e03-setup";

    [Theory]
    [InlineData(null, 5)]
    [InlineData("7", 7)]
    public async Task W01_EachIntervalTickReconcilesThePersistedExpiredOffer(string? interval, int seconds)
    {
        var database = await ReadyAsync(postgres);
        var (a, b) = await OfferAndQueueAsync(database);
        var before = await database.RowsAsync(StateSql);
        var clock = Clock(T0.AddSeconds(58));
        var tracker = new SessionTracker();
        await using var host = Host(database, clock, tracker, settings: interval is null ? null : new() { ["Laundry:QueueWorkerIntervalSeconds"] = interval });
        _ = host.Services;

        clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
        await Task.Delay(200);
        Assert.Equal(0, tracker.Opened);
        Assert.Equal(before, await database.RowsAsync(StateSql));

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(() => tracker.Disposed == 1);

        var at = 58 + seconds;
        Assert.Equal(1, tracker.Opened);
        Assert.Equal(
            [
                $"item|{a}|queued|3|12:01:{at - 60:00}.000000|null|null|null|null",
                $"item|{b}|to_be_confirmed|2|12:00:01.000000|{WasherId}|12:01:{at - 60:00}.000000|12:02:{at - 60:00}.000000|null",
            ],
            (await database.RowsAsync(StateSql)).Where(row => row.StartsWith("item|", StringComparison.Ordinal)).OrderBy(row => row.Contains(a.ToString(), StringComparison.Ordinal) ? 0 : 1));
    }

    [Fact]
    public async Task W02_EveryIterationRunsInItsOwnScopeThatIsDisposedAfterwards()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var tracker = new SessionTracker();
        await using var host = Host(database, clock, tracker);
        _ = host.Services;

        clock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => tracker.Disposed == 1);
        clock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => tracker.Disposed == 2);

        Assert.Equal(2, tracker.Opened);
        Assert.Equal(2, tracker.Instances.Distinct().Count());
        Assert.All(tracker.Instances, instance => Assert.True(instance.IsDisposed));
    }

    [Fact]
    public async Task W03_AFailedIterationIsLoggedAndWaitsForTheNextTickWithoutBusySpinning()
    {
        var database = await ReadyAsync(postgres);
        var (a, b) = await OfferAndQueueAsync(database);
        var clock = Clock(T0.AddSeconds(61));
        var tracker = new SessionTracker(failures: 2);
        var logs = new CapturingLoggerProvider();
        await using var host = Host(database, clock, tracker, logs);
        _ = host.Services;

        clock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => FailureLogs(logs).Count == 1);
        await Task.Delay(500);
        Assert.Equal(1, tracker.Opened);
        clock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => FailureLogs(logs).Count == 2);
        await Task.Delay(500);
        Assert.Equal(2, tracker.Opened);
        Assert.Equal($"to_be_confirmed|{WasherId}", await ItemAsync(database, a));

        clock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(async () => await ItemAsync(database, b) == $"to_be_confirmed|{WasherId}");

        Assert.Equal(3, tracker.Opened);
        Assert.Equal("queued|null", await ItemAsync(database, a));
        var failure = FailureLogs(logs)[0];
        Assert.Equal(LogLevel.Error, failure.Level);
        Assert.IsType<DatabaseUnavailableException>(failure.Exception);
        Assert.Equal(typeof(OfferReconciliationWorker).FullName, failure.Category);
    }

    [Fact]
    public async Task W04_ShutdownCancelsTheIterationWaitingOnTheDatabaseAndKeepsTheState()
    {
        var database = await ReadyAsync(postgres);
        await OfferAndQueueAsync(database);
        var before = await database.RowsAsync(StateSql);
        var clock = Clock(T0.AddSeconds(61));
        var logs = new CapturingLoggerProvider();
        var host = Host(database, clock, logs: logs);
        _ = host.Services;
        var (holder, transaction) = await HoldAsync(database, HolderApp, $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE");

        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilWaitingOnLockAsync(database, WorkerApp);
        await host.DisposeAsync().AsTask().WaitAsync(SignalTimeout);

        Assert.Equal(0L, await database.ScalarAsync<long>($"SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND application_name = '{WorkerApp}' AND wait_event_type = 'Lock'"));
        Assert.Contains(logs.Entries, log => log.EventId.Id == 321);
        Assert.Empty(FailureLogs(logs));
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task W05_RepeatedIterationsAndTwoHostsRequeueAnExpiredOfferOnlyOnce()
    {
        var database = await ReadyAsync(postgres);
        var (a, b) = await OfferAndQueueAsync(database);
        var firstClock = Clock(T0.AddSeconds(61));
        var secondClock = Clock(T0.AddSeconds(61));
        var first = new SessionTracker();
        var second = new SessionTracker();
        await using var firstHost = Host(database, firstClock, first);
        await using var secondHost = Host(database, secondClock, second);
        _ = firstHost.Services;
        _ = secondHost.Services;

        firstClock.Advance(TimeSpan.FromSeconds(5));
        secondClock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => first.Disposed == 1 && second.Disposed == 1);
        firstClock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => first.Disposed == 2);

        Assert.Equal(3L, await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq"));
        Assert.Equal("queued|null", await ItemAsync(database, a));
        Assert.Equal($"to_be_confirmed|{WasherId}", await ItemAsync(database, b));
        Assert.Equal("12:01:06", await database.ScalarAsync<string>($"SELECT to_char(offered_at AT TIME ZONE 'UTC', 'HH24:MI:SS') FROM service_request_items WHERE id = '{b}'"));
    }

    [Fact]
    public async Task W06_ARestartedHostRecoversTheQueueAndOfferFromPostgres()
    {
        var database = await ReadyAsync(postgres);
        var (a, b) = await OfferAndQueueAsync(database);
        var before = await database.RowsAsync(StateSql);
        var firstClock = Clock(T0.AddSeconds(30));
        var firstTracker = new SessionTracker();
        await using (var first = Host(database, firstClock, firstTracker))
        {
            _ = first.Services;
            firstClock.Advance(TimeSpan.FromSeconds(5));
            await EventuallyAsync(() => firstTracker.Disposed == 1);
        }

        Assert.Equal(before, await database.RowsAsync(StateSql));

        var secondClock = Clock(T0.AddSeconds(95));
        var secondTracker = new SessionTracker();
        await using var second = Host(database, secondClock, secondTracker);
        _ = second.Services;
        secondClock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(async () => await ItemAsync(database, b) == $"to_be_confirmed|{WasherId}");

        Assert.Equal("queued|null", await ItemAsync(database, a));
        Assert.Equal("12:01:40|12:02:40", await database.ScalarAsync<string>(
            $"SELECT to_char(offered_at AT TIME ZONE 'UTC', 'HH24:MI:SS') || '|' || to_char(expires_at AT TIME ZONE 'UTC', 'HH24:MI:SS') FROM service_request_items WHERE id = '{b}'"));
    }

    [Fact]
    public async Task W07_ALateWorkerDoesNotExtendTheConfirmationDeadline()
    {
        var database = await ReadyAsync(postgres);
        var (a, b) = await OfferAndQueueAsync(database);
        var booking = await database.ScalarAsync<Guid>($"SELECT request_id FROM service_request_items WHERE id = '{a}'");
        var clock = Clock(T0.AddSeconds(56));
        var tracker = new SessionTracker();
        await using var host = Host(database, clock, tracker);
        _ = host.Services;

        // O próximo tick é em 61 s; aos 60 s a oferta já venceu, com ou sem worker.
        clock.Advance(TimeSpan.FromSeconds(4));
        var confirm = await new ConfirmOfferUseCase(Sessions(database, SetupApp), Coordinator(clock), Metrics(), clock, NullLogger<ConfirmOfferUseCase>.Instance)
            .ExecuteAsync(booking, a, CancellationToken.None);

        Assert.Equal(0, tracker.Opened);
        Assert.Equal(ConfirmOfferOutcome.Expired, confirm.Outcome);
        Assert.Equal($"to_be_confirmed|{WasherId}", await ItemAsync(database, b));
        var afterConfirm = await database.RowsAsync(StateSql);

        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => tracker.Disposed == 1);

        Assert.Equal(afterConfirm, await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task W08_QueueMetricsOfTheWorkerAreExportedOverOtlpWithoutIdentifiers()
    {
        var database = await ReadyAsync(postgres);
        var (a, b) = await OfferAndQueueAsync(database);
        using var receiver = OtlpReceiver.Start();
        var clock = Clock(T0.AddSeconds(61));
        var tracker = new SessionTracker();
        var host = Host(database, clock, tracker, settings: new() { ["OpenTelemetry:OtlpEndpoint"] = receiver.Endpoint.AbsoluteUri });
        _ = host.Services;

        clock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => tracker.Disposed == 1);
        await host.DisposeAsync();

        var metrics = receiver.Requests.Where(request => request.Path == "/v1/metrics").ToList();
        Assert.Contains(metrics, request => Has(request.Body, "cicclo_offers_expired_total") && Has(request.Body, "machine_type") && Has(request.Body, "washer"));
        Assert.Contains(metrics, request => Has(request.Body, "cicclo_queue_wait_seconds"));
        Assert.All(metrics, request =>
        {
            Assert.False(Has(request.Body, a.ToString()));
            Assert.False(Has(request.Body, b.ToString()));
        });
    }

    [Fact]
    public async Task W09_TheNormalHostRunsTheWorkerOnTheSystemClockWithTheDefaultFiveSecondInterval()
    {
        var database = await ReadyAsync(postgres);
        await using var host = ApiFactory.Create(database.ConnectionString);

        Assert.Same(TimeProvider.System, host.Services.GetRequiredService<TimeProvider>());
        Assert.Single(host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<OfferReconciliationWorker>());
        Assert.Equal(5, host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Cicclo.Api.Platform.LaundryOptions>>().Value.QueueWorkerIntervalSeconds);
        using var scope = host.Services.CreateScope();
        Assert.IsType<PostgresLaundrySessions>(scope.ServiceProvider.GetRequiredService<ILaundrySessions>());
        var factoryContext = await host.Services.GetRequiredService<IDbContextFactory<CiccloDbContext>>().CreateDbContextAsync();
        Assert.Equal(database.ConnectionString, factoryContext.Database.GetConnectionString());
        await factoryContext.DisposeAsync();
    }

    private static WebApplicationFactory<Program> Host(
        TestDatabase database, FakeTimeProvider clock, SessionTracker? tracker = null, CapturingLoggerProvider? logs = null, Dictionary<string, string>? settings = null) =>
        ApiFactory.Create(Named(database, WorkerApp), settings).WithWebHostBuilder(host =>
        {
            if (logs is not null)
            {
                host.ConfigureLogging(logging => logging.AddProvider(logs));
            }

            host.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                if (tracker is not null)
                {
                    services.AddScoped<ILaundrySessions>(provider => tracker.Track(
                        new PostgresLaundrySessions(provider.GetRequiredService<IDbContextFactory<CiccloDbContext>>())));
                }
            });
        });

    /// <summary>A ofertada na lavadora em T0 (ticket 1, prazo 12:01:00); B na fila em T0+1 s (ticket 2).</summary>
    private static async Task<(Guid A, Guid B)> OfferAndQueueAsync(TestDatabase database)
    {
        var a = await PendingAsync(database);
        var b = await PendingAsync(database);
        await ActivateAsync(Sessions(database, SetupApp), Coordinator(Clock()), a);
        await ActivateAsync(Sessions(database, SetupApp), Coordinator(Clock(T0.AddSeconds(1))), b);
        return (a, b);
    }

    private static async Task<string> ItemAsync(TestDatabase database, Guid item) =>
        await database.ScalarAsync<string>($"SELECT status || '|' || coalesce(machine_id::text, 'null') FROM service_request_items WHERE id = '{item}'");

    private static List<CapturedLog> FailureLogs(CapturingLoggerProvider logs) => [.. logs.Entries.Where(log => log.EventId.Id == 320)];

    private static bool Has(byte[] body, string text) => body.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    /// <summary>
    /// Prazo real de 3 s, menor que qualquer intervalo do worker (5 s ou 7 s): a iteração observada só pode ter sido
    /// disparada pelo relógio falso, nunca por um timer do relógio do sistema.
    /// </summary>
    private static readonly TimeSpan TickTimeout = TimeSpan.FromSeconds(3);

    private static Task EventuallyAsync(Func<bool> condition) => EventuallyAsync(() => Task.FromResult(condition()));

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TickTimeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condição do worker não foi observada no prazo.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Registra cada adaptador de sessões criado no escopo de uma iteração e o seu descarte pelo escopo; opcionalmente
    /// faz as primeiras aberturas falharem como banco indisponível. Transações, locks e commits continuam reais.
    /// </summary>
    private sealed class SessionTracker(int failures = 0)
    {
        private readonly List<TrackedSessions> instances = [];
        private readonly int failures = failures;
        private int opened;

        public int Opened => Volatile.Read(ref opened);

        public int Disposed
        {
            get
            {
                lock (instances)
                {
                    return instances.Count(instance => instance.IsDisposed);
                }
            }
        }

        public IReadOnlyList<TrackedSessions> Instances
        {
            get
            {
                lock (instances)
                {
                    return [.. instances];
                }
            }
        }

        public TrackedSessions Track(ILaundrySessions inner)
        {
            var tracked = new TrackedSessions(this, inner);
            lock (instances)
            {
                instances.Add(tracked);
            }

            return tracked;
        }

        public sealed class TrackedSessions(SessionTracker tracker, ILaundrySessions inner) : ILaundrySessions, IAsyncDisposable
        {
            private int disposed;

            public bool IsDisposed => Volatile.Read(ref disposed) == 1;

            public Task<ILaundrySession> OpenAsync(CancellationToken cancellationToken)
            {
                var attempt = Interlocked.Increment(ref tracker.opened);
                return attempt <= tracker.failures
                    ? Task.FromException<ILaundrySession>(new DatabaseUnavailableException($"Falha transitória {attempt}."))
                    : inner.OpenAsync(cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref disposed, 1);
                return ValueTask.CompletedTask;
            }
        }
    }
}
