using System.Globalization;
using Cicclo.Api.Bookings;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Bookings;

/// <summary>
/// Caso de uso de compra contra PostgreSQL 18.1 real: valores esperados vêm da spec (FIN/IDEM), e cada
/// cenário confere o banco, não só o resultado. Interleavings são controlados por sinais e pelo estado
/// de espera de lock em pg_stat_activity, nunca por pausas fixas.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E02")]
[Trait("Task", "T3")]
public sealed class CreateBookingTests(PostgresFixture postgres)
{
    private const string BookingApp = "cicclo-e02-booking";
    private const string OutageApp = "cicclo-e02-outage";
    private const string IntruderApp = "cicclo-e02-intruder";
    private const string CompetitorApp = "cicclo-e02-competitor";
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(20);

    private const string StateSql =
        """
        SELECT line FROM (
            SELECT 1 AS rank, 'wallet|' || balance::text || '|' || to_char(updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US') AS line, '' AS k FROM wallets
            UNION ALL SELECT 2, 'request|' || id || '|' || idempotency_key || '|' || total_amount::text || '|' || request_fingerprint, id::text FROM service_requests
            UNION ALL SELECT 3, 'item|' || request_id || '|' || service_code || '|' || unit_price::text || '|' || status, id::text FROM service_request_items
            UNION ALL SELECT 4, 'ledger|' || request_id || '|' || kind || '|' || amount::text || '|' || balance_after::text || '|' || coalesce(request_item_id::text, 'null'), id::text FROM wallet_transactions
            UNION ALL SELECT 5, 'machine|' || name || '|' || state, '' FROM machines
            UNION ALL SELECT 6, 'service|' || code || '|' || current_price::text || '|' || is_active::text, '' FROM services
        ) AS state ORDER BY rank, line, k
        """;

    [Theory]
    [InlineData("K01", new[] { "wash" }, 1890L, 3110L, "31.10")]
    [InlineData("K02", new[] { "dry" }, 2090L, 2910L, "29.10")]
    [InlineData("K03", new[] { "wash", "dry" }, 3980L, 1020L, "10.20")]
    [InlineData("K04", new[] { "wash", "wash" }, 3780L, 1220L, "12.20")]
    public async Task K01_K04_PurchaseDebitsTheCatalogTotalAndPersistsBookingItemsAndLedger(
        string scenario, string[] codes, long totalCents, long remainingCents, string remaining)
    {
        var database = await ReadyAsync();
        var key = Guid.NewGuid();
        var intent = Intent(codes, totalCents);

        var result = await UseCase(Real(database)).ExecuteAsync(key, intent, CancellationToken.None);

        Assert.True(result.Outcome == CreateBookingOutcome.Created, scenario);
        var booking = result.Booking!;
        Assert.Equal(totalCents, MoneyCents.FromDecimal(booking.ChargedAmount));
        Assert.Equal(remainingCents, MoneyCents.FromDecimal(booking.RemainingBalance));
        Assert.Equal(
            codes.Order(StringComparer.Ordinal).Select(code => code == "wash" ? "wash|18.90|PendingActivation" : "dry|20.90|PendingActivation"),
            booking.Items.Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.ServiceCode}|{item.UnitPrice}|{item.Status}")));
        Assert.Equal(codes.Length, booking.Items.Select(item => item.ItemId).Distinct().Count());
        Assert.Equal(DateTimeKind.Utc, booking.CreatedAt.Kind);

        var charged = booking.ChargedAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"{booking.BookingId}|{key}|{charged}|{intent.Fingerprint}", (await database.RowsAsync("SELECT id, idempotency_key, total_amount::text, request_fingerprint FROM service_requests")).Single());
        Assert.Equal(
            booking.Items.Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.ItemId}|{booking.BookingId}|{item.ServiceCode}|{item.UnitPrice:0.00}|pending_activation")).Order(StringComparer.Ordinal),
            await database.RowsAsync("SELECT id, request_id, service_code, unit_price::text, status FROM service_request_items ORDER BY id::text"));
        Assert.Equal(
            $"{booking.BookingId}|debit|{charged}|{remaining}|True",
            (await database.RowsAsync("SELECT request_id, kind, amount::text, balance_after::text, request_item_id IS NULL FROM wallet_transactions")).Single());
        Assert.Equal(remaining, await database.ScalarAsync<string>("SELECT balance::text FROM wallets"));
        // FIN-11: total do booking = soma dos preços históricos = valor do débito; datas da decisão coincidem.
        Assert.True(await database.ScalarAsync<bool>(
            """
            SELECT r.total_amount = (SELECT sum(unit_price) FROM service_request_items)
               AND r.total_amount = (SELECT amount FROM wallet_transactions)
               AND r.created_at = (SELECT created_at FROM wallet_transactions)
               AND r.created_at = (SELECT updated_at FROM wallets)
            FROM service_requests r
            """));
        Assert.Equal(booking.CreatedAt, (await database.ScalarAsync<DateTime>("SELECT created_at FROM service_requests")).ToUniversalTime());

        var replay = await UseCase(Real(database)).ExecuteAsync(key, intent, CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Replayed, replay.Outcome);
        Assert.Equal(Describe(booking), Describe(replay.Booking!));
    }

    [Fact]
    public async Task K05_InsufficientBalanceRejectsWithoutAnyMutation()
    {
        var database = await ReadyAsync("10.00");
        var before = await database.RowsAsync(StateSql);

        var result = await UseCase(Real(database)).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Rejected, result.Outcome);
        Assert.Equal(BookingRejection.InsufficientBalance, result.Rejection);
        Assert.Null(result.Booking);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Contains("wallet|10.00|", before[0]);
    }

    [Fact]
    public async Task K06_DivergentExpectedPriceRejectsWithoutMutationAndDoesNotConsumeTheKey()
    {
        var database = await ReadyAsync();
        var before = await database.RowsAsync(StateSql);
        var key = Guid.NewGuid();

        var rejected = await UseCase(Real(database)).ExecuteAsync(key, Intent(["wash"], 1800), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Rejected, rejected.Outcome);
        Assert.Equal(BookingRejection.PriceChanged, rejected.Rejection);
        Assert.Equal(1890L, rejected.CalculatedTotalCents);
        Assert.Equal(before, await database.RowsAsync(StateSql));

        var confirmed = await UseCase(Real(database)).ExecuteAsync(key, Intent(["wash"], 1890), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Created, confirmed.Outcome);
        Assert.Equal(3110L, MoneyCents.FromDecimal(confirmed.Booking!.RemainingBalance));
    }

    [Theory]
    [InlineData("", new[] { "wash", "iron" }, 3780L, BookingRejection.ServiceNotFound)]
    [InlineData("UPDATE services SET is_active = false WHERE code = 'dry'", new[] { "wash", "dry" }, 3980L, BookingRejection.ServiceDisabled)]
    [InlineData("UPDATE machines SET state = 'out_of_service' WHERE machine_type = 'dryer'", new[] { "dry", "wash" }, 3980L, BookingRejection.NoOperationalMachine)]
    public async Task K07_AnyInvalidInactiveOrUnservedItemRejectsTheWholeBatch(string setup, string[] codes, long expected, BookingRejection rejection)
    {
        var database = await ReadyAsync();
        if (setup.Length > 0)
        {
            await database.ExecuteAsync(setup);
        }

        var before = await database.RowsAsync(StateSql);

        var result = await UseCase(Real(database)).ExecuteAsync(Guid.NewGuid(), Intent(codes, expected), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Rejected, result.Outcome);
        Assert.Equal(rejection, result.Rejection);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM service_request_items"));
    }

    [Theory]
    [InlineData("service_request_items", "RAISE EXCEPTION 'falha injetada no item'", "P0001")]
    [InlineData("wallet_transactions", "RAISE EXCEPTION 'falha injetada no ledger'", "P0001")]
    [InlineData("wallet_transactions", "RAISE unique_violation USING CONSTRAINT = 'ux_wallet_transactions_request_debit'", "23505")]
    public async Task K08_FailureWhileSavingItemsOrLedgerRevertsTheDebitAndEverything(string table, string raise, string sqlState)
    {
        var database = await ReadyAsync();
        await database.ExecuteAsync(
            $"""
            CREATE FUNCTION fail_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN {raise}; END $$;
            CREATE TRIGGER fail_insert BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION fail_insert();
            """);
        var before = await database.RowsAsync(StateSql);
        var sessions = new ScriptedBookingSessions(Real(database));

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            UseCase(sessions).ExecuteAsync(Guid.NewGuid(), Intent(["wash", "dry"], 3980), CancellationToken.None));

        Assert.Equal(sqlState, FindPostgres(error)?.SqlState);
        Assert.IsNotType<CreateBookingStoreException>(error);
        // Uma única tentativa: falha desconhecida não é repetida; 23505 de outra constraint não vira replay.
        Assert.Equal(1, sessions.Opened);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Contains("wallet|50.00|", before[0]);
    }

    [Fact]
    public async Task K09_ThreeConcurrentWashPurchasesWithDistinctKeysConfirmExactlyTwo()
    {
        var database = await ReadyAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var purchases = Enumerable.Range(0, 3)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return await UseCase(Real(database)).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None);
            }))
            .ToList();

        gate.SetResult();
        var results = await Task.WhenAll(purchases).WaitAsync(SignalTimeout);

        Assert.Equal(2, results.Count(result => result.Outcome == CreateBookingOutcome.Created));
        Assert.Single(results, result => result.Outcome == CreateBookingOutcome.Rejected && result.Rejection == BookingRejection.InsufficientBalance);
        Assert.Equal("12.20", await database.ScalarAsync<string>("SELECT balance::text FROM wallets"));
        Assert.Equal(["31.10", "12.20"], await database.RowsAsync("SELECT balance_after::text FROM wallet_transactions ORDER BY balance_after DESC"));
        Assert.Equal("2|2|2", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM service_request_items), (SELECT count(*) FROM wallet_transactions WHERE kind = 'debit')")).Single());
    }

    [Fact]
    public async Task K10_TwentyConcurrentCallsWithTheSameKeyCreateOneBookingAndOneDebit()
    {
        var database = await ReadyAsync();
        var key = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 20)
            .Select(index => Task.Run(async () =>
            {
                await gate.Task;
                // Variações canônicas equivalentes da mesma intenção.
                var codes = index % 2 == 0 ? new[] { "wash", "dry" } : new[] { "dry", "wash" };
                return await UseCase(Real(database)).ExecuteAsync(key, Intent(codes, 3980), CancellationToken.None);
            }))
            .ToList();

        gate.SetResult();
        var results = await Task.WhenAll(calls).WaitAsync(SignalTimeout);

        Assert.Single(results, result => result.Outcome == CreateBookingOutcome.Created);
        Assert.Equal(19, results.Count(result => result.Outcome == CreateBookingOutcome.Replayed));
        Assert.Single(results.Select(result => result.Booking!.BookingId).Distinct());
        Assert.All(results, result => Assert.Equal(1020L, MoneyCents.FromDecimal(result.Booking!.RemainingBalance)));
        Assert.Equal("1|2|1|10.20", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM service_request_items), (SELECT count(*) FROM wallet_transactions), (SELECT balance::text FROM wallets)")).Single());
    }

    [Fact]
    public async Task K11_ReplayRecoversTheOriginalPurchaseBeforeDynamicRulesAfterPriceCapacityAndBalanceChange()
    {
        var database = await ReadyAsync();
        var key = Guid.NewGuid();
        var created = await UseCase(Real(database)).ExecuteAsync(key, Intent(["wash", "dry"], 3980), CancellationToken.None);
        await database.ExecuteAsync(
            """
            UPDATE services SET current_price = 25.00 WHERE code = 'wash';
            UPDATE services SET is_active = false WHERE code = 'dry';
            UPDATE machines SET state = 'out_of_service';
            UPDATE wallets SET balance = 0.00;
            """);

        var replay = await UseCase(Real(database)).ExecuteAsync(key, Intent(["dry", "wash"], 3980), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Replayed, replay.Outcome);
        Assert.Equal(created.Booking!.BookingId, replay.Booking!.BookingId);
        Assert.Equal(3980L, MoneyCents.FromDecimal(replay.Booking.ChargedAmount));
        Assert.Equal(0L, MoneyCents.FromDecimal(replay.Booking.RemainingBalance));
        Assert.Equal(created.Booking.CreatedAt, replay.Booking.CreatedAt);
        Assert.Equal(
            ["dry|20.90|PendingActivation", "wash|18.90|PendingActivation"],
            replay.Booking.Items.Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.ServiceCode}|{item.UnitPrice}|{item.Status}")));
        Assert.Equal("1|2|1|0.00", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM service_request_items), (SELECT count(*) FROM wallet_transactions), (SELECT balance::text FROM wallets)")).Single());
    }

    [Theory]
    [InlineData(new[] { "wash", "wash" }, 3780L, null)]
    [InlineData(new[] { "wash", "dry", "dry" }, 6070L, null)]
    [InlineData(new[] { "wash", "dry" }, 3980L, "wash")]
    [InlineData(new[] { "wash", "dry" }, 3981L, null)]
    public async Task K11_SameKeyWithADifferentFingerprintConflictsWithoutMutation(string[] codes, long expected, string? preferred)
    {
        var database = await ReadyAsync();
        var key = Guid.NewGuid();
        Assert.Equal(CreateBookingOutcome.Created, (await UseCase(Real(database)).ExecuteAsync(key, Intent(["wash", "dry"], 3980), CancellationToken.None)).Outcome);
        var before = await database.RowsAsync(StateSql);

        var result = await UseCase(Real(database)).ExecuteAsync(key, Intent(codes, expected, preferred), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Rejected, result.Outcome);
        Assert.Equal(BookingRejection.IdempotencyConflict, result.Rejection);
        Assert.Null(result.Booking);
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task K12a_PurchaseHoldingTheCapacityLockCompletesBeforeAConcurrentOutage()
    {
        var database = await ReadyAsync();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new ScriptedBookingSessions(Real(database))
        {
            AfterLockCatalog = async _ =>
            {
                paused.TrySetResult();
                await release.Task;
            },
        };
        var purchase = UseCase(sessions).ExecuteAsync(Guid.NewGuid(), Intent(["dry"], 2090), CancellationToken.None);
        await paused.Task.WaitAsync(SignalTimeout);

        var outage = ExecuteAsAsync(database, OutageApp, $"UPDATE machines SET state = 'out_of_service', updated_at = now() WHERE id = '{DemoData.DryerId}'");
        await WaitUntilWaitingOnLockAsync(database, OutageApp);
        Assert.False(outage.IsCompleted);
        release.SetResult();
        var result = await purchase.WaitAsync(SignalTimeout);
        await outage.WaitAsync(SignalTimeout);

        Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
        Assert.Equal("29.10|1|out_of_service", (await database.RowsAsync(
            $"SELECT (SELECT balance::text FROM wallets), (SELECT count(*) FROM service_request_items WHERE service_code = 'dry'), (SELECT state FROM machines WHERE id = '{DemoData.DryerId}')")).Single());
    }

    [Fact]
    public async Task K12b_PurchaseWaitingForACommittedOutageIsRejectedForLostCapacity()
    {
        var database = await ReadyAsync();
        await using var outage = new NpgsqlConnection(Named(database, OutageApp));
        await outage.OpenAsync();
        await using var transaction = await outage.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand($"UPDATE machines SET state = 'out_of_service', updated_at = now() WHERE id = '{DemoData.DryerId}'", outage, transaction))
        {
            await update.ExecuteNonQueryAsync();
        }

        var purchase = UseCase(Real(database)).ExecuteAsync(Guid.NewGuid(), Intent(["dry"], 2090), CancellationToken.None);
        await WaitUntilWaitingOnLockAsync(database, BookingApp);
        Assert.False(purchase.IsCompleted);
        await transaction.CommitAsync();
        var result = await purchase.WaitAsync(SignalTimeout);

        Assert.Equal(CreateBookingOutcome.Rejected, result.Outcome);
        Assert.Equal(BookingRejection.NoOperationalMachine, result.Rejection);
        Assert.Equal("50.00|0|0", (await database.RowsAsync(
            "SELECT (SELECT balance::text FROM wallets), (SELECT count(*) FROM service_requests), (SELECT count(*) FROM wallet_transactions)")).Single());
    }

    [Fact]
    public async Task K12c_RealDeadlockWithKnownRollbackIsRetriedAsAWholeUnitAndChargesOnce()
    {
        var database = await ReadyAsync();
        var name = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database;
        // Conexões novas deste banco detectam deadlock em 50 ms; o intruso abaixo espera 60 s, então a compra é a vítima.
        await database.ExecuteAsync($"ALTER DATABASE \"{name}\" SET deadlock_timeout = '50ms'");
        await using var intruder = new NpgsqlConnection(Named(database, IntruderApp));
        await intruder.OpenAsync();
        await using (var slow = new NpgsqlCommand("SET deadlock_timeout = '60s'", intruder))
        {
            await slow.ExecuteNonQueryAsync();
        }

        await using var transaction = await intruder.BeginTransactionAsync();
        await using (var machine = new NpgsqlCommand($"SELECT 1 FROM machines WHERE id = '{DemoData.WasherId}' FOR UPDATE", intruder, transaction))
        {
            await machine.ExecuteScalarAsync();
        }

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new ScriptedBookingSessions(Real(database))
        {
            AfterLockWallet = async attempt =>
            {
                if (attempt == 1)
                {
                    paused.TrySetResult();
                    await release.Task;
                }
            },
        };
        var purchase = UseCase(sessions).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None);
        await paused.Task.WaitAsync(SignalTimeout);
        await using var walletCommand = new NpgsqlCommand($"SELECT balance FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE", intruder, transaction);
        var walletLock = walletCommand.ExecuteScalarAsync();
        await WaitUntilWaitingOnLockAsync(database, IntruderApp);

        release.SetResult();
        Assert.Equal(50.00m, await walletLock.WaitAsync(SignalTimeout));
        await transaction.CommitAsync();
        var result = await purchase.WaitAsync(SignalTimeout);

        Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
        Assert.Equal(2, sessions.Opened);
        var failure = Assert.IsType<CreateBookingStoreException>(Assert.Single(sessions.Failures));
        Assert.Equal(CreateBookingStoreFailure.RetryableConflict, failure.Failure);
        Assert.Equal(PostgresErrorCodes.DeadlockDetected, FindPostgres(failure)?.SqlState);
        Assert.Equal("1|1|31.10", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM wallet_transactions), (SELECT balance::text FROM wallets)")).Single());
    }

    [Fact]
    public async Task K12d_RetryableConflictsStopAfterThreeTotalAttemptsWithoutMutation()
    {
        var database = await ReadyAsync();
        var before = await database.RowsAsync(StateSql);
        var sessions = new ScriptedBookingSessions(Real(database))
        {
            // Depois do débito real, dentro da transação: o rollback desfaz o que já foi gravado.
            AfterDebit = _ => throw new CreateBookingStoreException(CreateBookingStoreFailure.RetryableConflict, "conflito injetado"),
        };

        var error = await Assert.ThrowsAsync<CreateBookingStoreException>(() =>
            UseCase(sessions).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None));

        Assert.Equal(CreateBookingStoreFailure.RetryableConflict, error.Failure);
        Assert.Equal(CreateBooking.MaxAttempts, sessions.Opened);
        Assert.Equal(3, sessions.Opened);
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task K12e_KnownIdempotencyUniqueViolationRollsBackAndRecoversByReplay()
    {
        var database = await ReadyAsync();
        var key = Guid.NewGuid();
        var created = await UseCase(Real(database)).ExecuteAsync(key, Intent(["wash"], 1890), CancellationToken.None);
        var sessions = new ScriptedBookingSessions(Real(database))
        {
            // Primeira sessão não enxerga a compra: a unique do banco é a última defesa.
            OnFind = (attempt, found) => attempt == 1 ? null : found,
        };

        var result = await UseCase(sessions).ExecuteAsync(key, Intent(["wash"], 1890), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Replayed, result.Outcome);
        Assert.Equal(created.Booking!.BookingId, result.Booking!.BookingId);
        Assert.Equal(2, sessions.Opened);
        var failure = Assert.IsType<CreateBookingStoreException>(Assert.Single(sessions.Failures));
        Assert.Equal(CreateBookingStoreFailure.IdempotencyKeyTaken, failure.Failure);
        Assert.Equal("ux_service_requests_wallet_idempotency_key", FindPostgres(failure)?.ConstraintName);
        Assert.Equal("1|1|31.10", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM wallet_transactions), (SELECT balance::text FROM wallets)")).Single());
    }

    [Fact]
    public async Task K12f_CommitWhoseResponseIsLostIsRecoveredByKeyWithoutChargingAgain()
    {
        var database = await ReadyAsync();
        var sessions = new ScriptedBookingSessions(Real(database))
        {
            OnCommit = async (attempt, commit) =>
            {
                await commit();
                throw new CreateBookingStoreException(CreateBookingStoreFailure.CommitOutcomeUnknown, "resposta do commit perdida");
            },
        };

        var result = await UseCase(sessions).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], 1890), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
        Assert.Equal(await database.ScalarAsync<Guid>("SELECT id FROM service_requests"), result.Booking!.BookingId);
        Assert.Equal(3110L, MoneyCents.FromDecimal(result.Booking.RemainingBalance));
        Assert.Equal(2, sessions.Opened);
        Assert.Equal("1|1|31.10", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM wallet_transactions), (SELECT balance::text FROM wallets)")).Single());
    }

    [Fact]
    public async Task K12g_UnknownCommitWithoutPersistedPurchaseIsInconclusiveAndKeepsTheKeyUsable()
    {
        var database = await ReadyAsync();
        var before = await database.RowsAsync(StateSql);
        var key = Guid.NewGuid();
        var lookups = new List<string>();
        var sessions = new ScriptedBookingSessions(Real(database))
        {
            // A conexão cai antes de o COMMIT chegar ao servidor: descartar a sessão faz rollback.
            OnCommit = (_, _) => throw new CreateBookingStoreException(CreateBookingStoreFailure.CommitOutcomeUnknown, "conexão perdida antes do commit"),
            OnFind = (attempt, found) =>
            {
                lock (lookups)
                {
                    lookups.Add($"{attempt}|{(found is null ? "not-found" : "found")}");
                }

                return found;
            },
        };

        // Rollback antes de reconsultar: a sessão de reconciliação obtém o lock da wallet logo, sem esperar
        // o timeout de comando (30 s) que só ocorreria com a transação original ainda aberta.
        var result = await UseCase(sessions).ExecuteAsync(key, Intent(["wash"], 1890), CancellationToken.None).WaitAsync(SignalTimeout);

        Assert.Equal(CreateBookingOutcome.Inconclusive, result.Outcome);
        Assert.Null(result.Booking);
        Assert.Null(result.Rejection);
        Assert.Equal(2, sessions.Opened);
        // A decisão vem da busca da chave na sessão nova (não encontrada), não de uma falha da reconciliação.
        Assert.Equal(["1|not-found", "2|not-found"], lookups);
        var failure = Assert.IsType<CreateBookingStoreException>(Assert.Single(sessions.Failures));
        Assert.Equal(CreateBookingStoreFailure.CommitOutcomeUnknown, failure.Failure);
        Assert.Equal(before, await database.RowsAsync(StateSql));

        var retry = await UseCase(Real(database)).ExecuteAsync(key, Intent(["wash"], 1890), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Created, retry.Outcome);
        Assert.Equal("1|1|31.10", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM wallet_transactions), (SELECT balance::text FROM wallets)")).Single());
    }

    // IDEM-03/IDEM-07: entre o rollback da tentativa original e a reconsulta, outra chamada com a mesma chave
    // confirma. A reconciliação acha uma compra que não é a desta tentativa: com a mesma intenção é replay sem
    // novo débito; com intenção diferente é IDEMPOTENCY_CONFLICT sem mutação. Nunca Created com a compra alheia.
    [Theory]
    [InlineData(new[] { "wash" }, 1890L, "31.10", CreateBookingOutcome.Replayed)]
    [InlineData(new[] { "dry" }, 2090L, "29.10", CreateBookingOutcome.Rejected)]
    public async Task K12h_UnknownCommitReconciledAgainstAConcurrentPurchaseWithTheSameKeyHonoursTheFingerprint(
        string[] codes, long expected, string balance, CreateBookingOutcome outcome)
    {
        var database = await ReadyAsync();
        var key = Guid.NewGuid();
        var competitorIntent = Intent(codes, expected);
        Task<CreateBookingResult>? competitor = null;
        IReadOnlyList<string>? afterCompetitor = null;
        var lookups = new List<string>();
        var gated = new GatedBookingSessions(Real(database), async opening =>
        {
            // A reconsulta só abre depois que a chamada concorrente confirmou.
            if (opening == 2)
            {
                await competitor!.WaitAsync(SignalTimeout);
                afterCompetitor = await database.RowsAsync(StateSql);
            }
        });
        var sessions = new ScriptedBookingSessions(gated)
        {
            OnCommit = async (_, _) =>
            {
                // Com a wallet ainda bloqueada por esta transação, a concorrente com a mesma chave entra na fila do lock;
                // a conexão cai antes de o COMMIT chegar ao servidor e o descarte da sessão faz rollback.
                competitor = UseCase(new PostgresCreateBookingSessions(database.ContextFactory(CompetitorApp)))
                    .ExecuteAsync(key, competitorIntent, CancellationToken.None);
                await WaitUntilWaitingOnLockAsync(database, CompetitorApp);
                throw new CreateBookingStoreException(CreateBookingStoreFailure.CommitOutcomeUnknown, "conexão perdida antes do commit");
            },
            OnFind = (attempt, found) =>
            {
                lock (lookups)
                {
                    lookups.Add($"{attempt}|{(found is null ? "not-found" : "found")}");
                }

                return found;
            },
        };

        var result = await UseCase(sessions).ExecuteAsync(key, Intent(["wash"], 1890), CancellationToken.None).WaitAsync(SignalTimeout);
        var other = await competitor!.WaitAsync(SignalTimeout);

        Assert.Equal(CreateBookingOutcome.Created, other.Outcome);
        Assert.Equal(2, sessions.Opened);
        Assert.Equal(["1|not-found", "2|found"], lookups);
        var failure = Assert.IsType<CreateBookingStoreException>(Assert.Single(sessions.Failures));
        Assert.Equal(CreateBookingStoreFailure.CommitOutcomeUnknown, failure.Failure);
        Assert.Equal(outcome, result.Outcome);
        if (outcome == CreateBookingOutcome.Replayed)
        {
            Assert.Null(result.Rejection);
            Assert.Equal(other.Booking!.BookingId, result.Booking!.BookingId);
            Assert.Equal(1890L, MoneyCents.FromDecimal(result.Booking.ChargedAmount));
            Assert.Equal(3110L, MoneyCents.FromDecimal(result.Booking.RemainingBalance));
            Assert.Equal(Describe(other.Booking), Describe(result.Booking));
        }
        else
        {
            Assert.Equal(BookingRejection.IdempotencyConflict, result.Rejection);
            Assert.Null(result.Booking);
        }

        // Só a compra concorrente existe: a tentativa original não deixou linha nem débito, e a reconciliação nada gravou.
        Assert.Equal(afterCompetitor, await database.RowsAsync(StateSql));
        Assert.Equal($"{other.Booking!.BookingId}|{competitorIntent.Fingerprint}", (await database.RowsAsync("SELECT id, request_fingerprint FROM service_requests")).Single());
        Assert.Equal($"{other.Booking.BookingId}|debit|{balance}", (await database.RowsAsync("SELECT request_id, kind, balance_after::text FROM wallet_transactions")).Single());
        Assert.Equal(balance, await database.ScalarAsync<string>("SELECT balance::text FROM wallets"));
    }

    // A16: numa intenção nova, serviço, capacidade e preço esperado são validados antes do saldo. Saldo 10.00
    // não cobre nem o total enviado (1800) nem o calculado (1890); 900 cobriria só o enviado.
    [Theory]
    [InlineData(1800L)]
    [InlineData(900L)]
    public async Task K13_DivergentExpectedPriceTakesPrecedenceOverInsufficientBalance(long expected)
    {
        var database = await ReadyAsync("10.00");
        var before = await database.RowsAsync(StateSql);

        var result = await UseCase(Real(database)).ExecuteAsync(Guid.NewGuid(), Intent(["wash"], expected), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Rejected, result.Outcome);
        Assert.Equal(BookingRejection.PriceChanged, result.Rejection);
        Assert.Equal(1890L, result.CalculatedTotalCents);
        Assert.Null(result.Booking);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Contains("wallet|10.00|", before[0]);
    }

    [Theory]
    [InlineData("", new[] { "wash", "iron" }, 3780L, BookingRejection.ServiceNotFound)]
    [InlineData("UPDATE services SET is_active = false WHERE code = 'dry'", new[] { "wash", "dry" }, 3980L, BookingRejection.ServiceDisabled)]
    [InlineData("UPDATE machines SET state = 'out_of_service' WHERE machine_type = 'dryer'", new[] { "dry", "wash" }, 3980L, BookingRejection.NoOperationalMachine)]
    public async Task K13_InvalidInactiveOrUnservedItemTakesPrecedenceOverInsufficientBalance(string setup, string[] codes, long expected, BookingRejection rejection)
    {
        var database = await ReadyAsync("10.00");
        if (setup.Length > 0)
        {
            await database.ExecuteAsync(setup);
        }

        var before = await database.RowsAsync(StateSql);

        var result = await UseCase(Real(database)).ExecuteAsync(Guid.NewGuid(), Intent(codes, expected), CancellationToken.None);

        Assert.Equal(CreateBookingOutcome.Rejected, result.Outcome);
        Assert.Equal(rejection, result.Rejection);
        Assert.Null(result.Booking);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Contains("wallet|10.00|", before[0]);
    }

    private async Task<TestDatabase> ReadyAsync(string? balance = null)
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        if (balance is not null)
        {
            await database.ExecuteAsync($"UPDATE wallets SET balance = {balance}");
        }

        return database;
    }

    private static CreateBooking UseCase(ICreateBookingSessions sessions) =>
        new(sessions, TimeProvider.System, NullLogger<CreateBooking>.Instance);

    private static PostgresCreateBookingSessions Real(TestDatabase database) => new(database.ContextFactory(BookingApp));

    private static BookingIntent Intent(string[] codes, long expectedTotalCents, string? preferred = null) =>
        BookingIntent.Create(codes, preferred, expectedTotalCents, 10).Intent!;

    private static string Describe(BookingSnapshot booking) =>
        string.Create(CultureInfo.InvariantCulture, $"{booking.BookingId}|{booking.ChargedAmount}|{booking.RemainingBalance}|{booking.CreatedAt:O}|")
        + string.Join(';', booking.Items.Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.ItemId}:{item.ServiceCode}:{item.UnitPrice}:{item.Status}")));

    private static string Named(TestDatabase database, string applicationName) =>
        new NpgsqlConnectionStringBuilder(database.ConnectionString) { ApplicationName = applicationName }.ConnectionString;

    private static async Task ExecuteAsAsync(TestDatabase database, string applicationName, string sql)
    {
        await using var connection = new NpgsqlConnection(Named(database, applicationName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Sinal do próprio PostgreSQL: a conexão nomeada está bloqueada esperando um lock de linha.</summary>
    private static async Task WaitUntilWaitingOnLockAsync(TestDatabase database, string applicationName)
    {
        var deadline = DateTime.UtcNow + SignalTimeout;
        while (await database.ScalarAsync<long>(
                   $"SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND application_name = '{applicationName}' AND wait_event_type = 'Lock'") == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"A conexão {applicationName} não ficou aguardando lock.");
            }

            await Task.Delay(20);
        }
    }

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

    /// <summary>Executa um sinal antes de abrir cada sessão (1 = primeira), sem tocar na transação.</summary>
    private sealed class GatedBookingSessions(ICreateBookingSessions inner, Func<int, Task> beforeOpen) : ICreateBookingSessions
    {
        private int opening;

        public async Task<ICreateBookingSession> OpenAsync(CancellationToken cancellationToken)
        {
            await beforeOpen(Interlocked.Increment(ref opening));
            return await inner.OpenAsync(cancellationToken);
        }
    }
}
