using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Http;

[Trait("Increment", "E01")]
[Trait("Task", "T3")]
public sealed class ErrorBoundaryTests
{
    private const string Secret = "Password=segredo-interno";
    private const string LeakySql = "SELECT balance FROM wallets";

    [Fact]
    public async Task P01_KnownErrorUsesStandardProblemWithTraceIdAndNullErrors()
    {
        await using var host = await IsolatedHttpHost.StartAsync(MapFailures);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/fail/unavailable");

        var body = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, "/fail/unavailable");
        Assert.Equal(JsonValueKind.Null, body.GetProperty("errors").ValueKind);
        Assert.DoesNotContain(Secret, body.GetRawText());
    }

    [Fact]
    public async Task P02_FieldErrorsAreReturnedAsAMap()
    {
        await using var host = await IsolatedHttpHost.StartAsync(app => app.MapGet(
            "/validation",
            (HttpContext context) => ApiProblems.Validation(context, new Dictionary<string, string[]> { ["serviceCodes"] = ["Informe ao menos um serviço."] })));
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/validation");

        var body = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.BadRequest, ApiProblems.ValidationError, "/validation");
        var errors = body.GetProperty("errors");
        Assert.Equal(["serviceCodes"], errors.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["Informe ao menos um serviço."], errors.GetProperty("serviceCodes").EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("connection-refused")]
    [InlineData("cannot-connect-now")]
    [InlineData("connection-failure")]
    [InlineData("missing-initial-table")]
    [InlineData("update-over-lost-connection")]
    [InlineData("ef-transient-wrapper")]
    public async Task P03_KnownUnavailabilityReturns503(string kind)
    {
        await using var host = await IsolatedHttpHost.StartAsync(MapFailures);
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"/fail/{kind}");

        var body = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, $"/fail/{kind}");
        Assert.DoesNotContain("127.0.0.1", body.GetRawText());
        Assert.Contains(host.Logs.Entries, log => log.Level == LogLevel.Warning && log.Category == typeof(ApiExceptionHandler).FullName && log.Exception is not null);
    }

    [Fact]
    public async Task P04_UnexpectedFailureReturnsSanitized500AndIsLoggedInternally()
    {
        await using var host = await IsolatedHttpHost.StartAsync(MapFailures);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/fail/unexpected");

        var body = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.InternalServerError, ApiProblems.InternalError, "/fail/unexpected");
        var raw = body.GetRawText();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("errors").ValueKind);
        foreach (var leak in new[] { Secret, LeakySql, "InvalidOperationException", " at ", "ErrorBoundaryTests" })
        {
            Assert.DoesNotContain(leak, raw);
        }

        var logged = Assert.Single(host.Logs.Entries, log => log.Level == LogLevel.Error && log.Category == typeof(ApiExceptionHandler).FullName);
        Assert.IsType<InvalidOperationException>(logged.Exception);
    }

    [Theory]
    [InlineData("unique-violation-on-save")]
    [InlineData("sql-syntax-error")]
    [InlineData("unknown-table")]
    [InlineData("invalid-operation-over-syntax-error")]
    public async Task P05_GenericPersistenceFailuresStay500(string kind)
    {
        await using var host = await IsolatedHttpHost.StartAsync(MapFailures);
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"/fail/{kind}");

        await ProblemAssert.HasProblemAsync(response, HttpStatusCode.InternalServerError, ApiProblems.InternalError, $"/fail/{kind}");
    }

    [Fact]
    public async Task P06_RequestAbortedByClientIsNotASystemic500()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await IsolatedHttpHost.StartAsync(
            app => app.MapGet("/slow", async (HttpContext context) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }),
            app => app.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                finally
                {
                    finished.TrySetResult(context.Response.StatusCode);
                }
            }));
        using var client = host.CreateClient();
        using var cancellation = new CancellationTokenSource();

        var request = client.GetAsync("/slow", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var status = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(StatusCodes.Status500InternalServerError, status);
        Assert.DoesNotContain(host.Logs.Entries, log => log.Level >= LogLevel.Error);
        Assert.DoesNotContain(host.Logs.Entries, log => log.Message.Contains(ApiProblems.InternalError, StringComparison.Ordinal));
    }

    private static void MapFailures(WebApplication app) =>
        app.MapGet("/fail/{kind}", async Task<IResult> (string kind) =>
        {
            switch (kind)
            {
                case "unavailable":
                    throw new DatabaseUnavailableException($"Wallet demo ausente; {Secret}");
                case "connection-refused":
                    await using (var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Timeout=2;Pooling=false"))
                    {
                        await connection.OpenAsync();
                    }

                    break;
                case "cannot-connect-now":
                    throw new PostgresException("the database system is starting up", "FATAL", "FATAL", PostgresErrorCodes.CannotConnectNow);
                case "connection-failure":
                    throw new PostgresException("connection failure", "FATAL", "FATAL", PostgresErrorCodes.ConnectionFailure);
                case "missing-initial-table":
                    throw new PostgresException("relation \"wallets\" does not exist", "ERROR", "ERROR", PostgresErrorCodes.UndefinedTable);
                case "update-over-lost-connection":
                    throw new DbUpdateException("save failed", new NpgsqlException("Exception while reading from stream", new IOException("connection reset")));
                case "ef-transient-wrapper":
                    throw new InvalidOperationException("transient failure", new NpgsqlException("Failed to connect", new System.Net.Sockets.SocketException()));
                case "unexpected":
                    throw new InvalidOperationException($"{LeakySql}; {Secret}");
                case "unique-violation-on-save":
                    throw new DbUpdateException("save failed", new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));
                case "invalid-operation-over-syntax-error":
                    throw new InvalidOperationException("query failed", new PostgresException("syntax error", "ERROR", "ERROR", PostgresErrorCodes.SyntaxError));
                case "sql-syntax-error":
                    throw new PostgresException("syntax error at or near \"SELEC\"", "ERROR", "ERROR", PostgresErrorCodes.SyntaxError);
                case "unknown-table":
                    throw new PostgresException("relation \"bookingz\" does not exist", "ERROR", "ERROR", PostgresErrorCodes.UndefinedTable);
            }

            return Results.Ok();
        });
}
