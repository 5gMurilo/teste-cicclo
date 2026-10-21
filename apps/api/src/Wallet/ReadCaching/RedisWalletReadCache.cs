using System.Diagnostics.Metrics;
using Cicclo.Api.Wallet.GetBalance;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace Cicclo.Api.Wallet.ReadCaching;

/// <summary>
/// Adaptador Redis do cache da projeção de saldo (CACHE-02/05/08, D07/D08). Um único multiplexer por instância,
/// criado sem bloquear na primeira operação; desconexão falha rápido (sem backlog) e toda espera tem o orçamento de
/// <see cref="WalletCacheOptions.OperationTimeoutMilliseconds"/>. Falhas viram miss/false com métrica e log, nunca
/// exceção para o fluxo PostgreSQL. Só a chave <see cref="Key"/> é lida, gravada ou removida.
/// </summary>
public sealed partial class RedisWalletReadCache : IWalletReadCache, IAsyncDisposable
{
    public const string Key = "wallet:demo:v1";

    private readonly WalletCacheOptions options;
    private readonly TimeSpan budget;
    private readonly Lazy<Task<ConnectionMultiplexer>> connection;
    private readonly Counter<long> operations;
    private readonly ILogger<RedisWalletReadCache> logger;

    public RedisWalletReadCache(IOptions<WalletCacheOptions> options, IMeterFactory meters, ILogger<RedisWalletReadCache> logger)
    {
        this.options = options.Value;
        this.logger = logger;
        budget = TimeSpan.FromMilliseconds(this.options.OperationTimeoutMilliseconds);
        var configuration = ConfigurationOptions.Parse(this.options.Redis ?? throw new InvalidOperationException("Caching:Redis não configurado."));
        configuration.AbortOnConnectFail = false;
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        configuration.ConnectRetry = 1;
        configuration.ConnectTimeout = this.options.OperationTimeoutMilliseconds;
        configuration.AsyncTimeout = this.options.OperationTimeoutMilliseconds;
        configuration.SyncTimeout = this.options.OperationTimeoutMilliseconds;
        connection = new(() => ConnectionMultiplexer.ConnectAsync(configuration), LazyThreadSafetyMode.ExecutionAndPublication);
        operations = meters.Create(RefundMetrics.MeterName)
            .CreateCounter<long>("cicclo_cache_operations_total", description: "Operações do cache de leitura da wallet por operation/outcome.");
    }

    public async Task<WalletBalance?> ReadAsync(CancellationToken cancellationToken)
    {
        var (completed, value) = await RunAsync("read", database => database.StringGetAsync(Key), cancellationToken);
        if (!completed)
        {
            return null;
        }

        if (value.IsNull)
        {
            Record("read", "miss");
            return null;
        }

        if (WalletCacheEntry.TryParse(value.ToString(), out var balance))
        {
            Record("read", "hit");
            return balance;
        }

        Degraded("read", "invalid", null);
        return null;
    }

    public async Task<bool> WriteAsync(WalletBalance balance, CancellationToken cancellationToken)
    {
        var json = WalletCacheEntry.Serialize(balance);
        if (json is null)
        {
            Degraded("write", "invalid", null);
            return false;
        }

        var ttl = TimeSpan.FromSeconds(options.TimeToLiveSeconds);
        var (completed, stored) = await RunAsync("write", database => database.StringSetAsync(Key, json, ttl, When.Always), cancellationToken);
        return Acknowledge("write", completed, stored);
    }

    public async Task<bool> InvalidateAsync(CancellationToken cancellationToken)
    {
        var (completed, _) = await RunAsync("invalidate", database => database.KeyDeleteAsync(Key), cancellationToken);
        return Acknowledge("invalidate", completed, true);
    }

    public async ValueTask DisposeAsync()
    {
        if (connection.IsValueCreated && connection.Value.IsCompletedSuccessfully)
        {
            await connection.Value.Result.DisposeAsync();
        }
    }

    private bool Acknowledge(string operation, bool completed, bool acknowledged)
    {
        if (completed && acknowledged)
        {
            Record(operation, "ok");
        }
        else if (completed)
        {
            Degraded(operation, "error", null);
        }

        return completed && acknowledged;
    }

    /// <summary>
    /// Executa um comando dentro do orçamento. WaitAsync não cancela um comando já enviado: a tarefa abandonada tem a
    /// falha observada para não gerar exceção não observada. Somente o cancelamento do chamador é propagado.
    /// </summary>
    private async Task<(bool Completed, T Value)> RunAsync<T>(string operation, Func<IDatabase, Task<T>> command, CancellationToken cancellationToken)
    {
        Task<T>? pending = null;
        try
        {
            var started = TimeProvider.System.GetTimestamp();
            var multiplexer = await Observed(connection.Value).WaitAsync(budget, cancellationToken);
            var remaining = budget - TimeProvider.System.GetElapsedTime(started);
            var database = multiplexer.GetDatabase();
            pending = Observed(command(options.KeyPrefix.Length == 0 ? database : database.WithKeyPrefix(options.KeyPrefix)));
            return (true, await pending.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TimeoutException or RedisTimeoutException)
        {
            Degraded(operation, "timeout", exception);
        }
        catch (Exception exception)
        {
            Degraded(operation, "error", exception);
        }

        return (false, default!);
    }

    private static Task<T> Observed<T>(Task<T> task)
    {
        _ = task.ContinueWith(static faulted => _ = faulted.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private void Degraded(string operation, string outcome, Exception? exception)
    {
        Record(operation, outcome);
        LogDegraded(logger, operation, outcome, exception?.GetType().Name ?? "none");
    }

    private void Record(string operation, string outcome) =>
        operations.Add(1, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Somente operação, desfecho e tipo da exceção: nem configuração, nem mensagem (que pode citar o endpoint).</summary>
    [LoggerMessage(EventId = 800, Level = LogLevel.Warning, Message = "Cache da wallet degradado: {Operation} terminou em {Outcome} ({ErrorType}); a leitura segue pelo PostgreSQL.")]
    private static partial void LogDegraded(ILogger logger, string operation, string outcome, string errorType);
}
