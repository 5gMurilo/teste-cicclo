using System.Globalization;
using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Wallet;

[Collection(PostgresCollection.Name)]
[Trait("Increment", "E01")]
[Trait("Task", "T6")]
public sealed class WalletEndpointTests(PostgresFixture postgres)
{
    private const string Path = "/api/v1/wallet";

    [Fact]
    public async Task W01_SeededWalletReturns5000BrlWithDatabaseUtcTimestamp()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal(["balanceCents", "currency", "updatedAt"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Number, root.GetProperty("balanceCents").ValueKind);
        Assert.Equal(5000L, root.GetProperty("balanceCents").GetInt64());
        Assert.Equal("BRL", root.GetProperty("currency").GetString());
        var updatedAt = root.GetProperty("updatedAt").GetString()!;
        Assert.EndsWith("Z", updatedAt);
        var persisted = await database.ScalarAsync<DateTime>($"SELECT updated_at FROM wallets WHERE id = '{DemoData.WalletId}'");
        Assert.Equal(persisted, DateTime.Parse(updatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        Assert.Equal(DateTimeKind.Utc, DateTime.Parse(updatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Kind);
    }

    [Fact]
    public async Task W02_ChangedPersistedBalanceIsReturnedInCents()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await database.ExecuteAsync($"UPDATE wallets SET balance = 31.10 WHERE id = '{DemoData.WalletId}';");
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(3110L, await BalanceCentsAsync(client, Path));
    }

    [Fact]
    public async Task W03_ForgedWalletOrUserSelectorsDoNotSwitchTheAccount()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        const string other = "00000000-0000-4000-8000-000000000002";
        await database.ExecuteAsync($"INSERT INTO wallets (id, currency, balance, created_at, updated_at) VALUES ('{other}', 'BRL', 99.99, now(), now());");
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Wallet-Id", other);
        client.DefaultRequestHeaders.Add("X-User-Id", other);

        Assert.Equal(5000L, await BalanceCentsAsync(client, $"{Path}?walletId={other}&userId={other}"));
        Assert.Equal(5000L, await BalanceCentsAsync(client, $"{Path}?WalletId={other}"));
    }

    [Fact]
    public async Task W04_MissingConnectionReturns503()
    {
        await using var factory = ApiFactory.Create(connectionString: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path);

        await ProblemAssert.HasProblemAsync(response, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Path);
    }

    [Fact]
    public async Task W05_MissingDemoWalletReturns503InsteadOfAnInventedBalance()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync();
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path);

        await ProblemAssert.HasProblemAsync(response, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Path);
    }

    [Fact]
    public async Task W06_DatabaseDownReturns503()
    {
        await using var factory = ApiFactory.Create(ApiFactory.UnreachableDatabase);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path);

        var body = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Path);
        Assert.DoesNotContain("nao-vazar-down", body.GetRawText());
    }

    [Fact]
    public async Task W07_UnexpectedFailureReturnsSanitized500()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await database.ExecuteAsync("ALTER TABLE wallets RENAME COLUMN balance TO balance_renamed;");
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path);

        var raw = (await ProblemAssert.HasProblemAsync(response, HttpStatusCode.InternalServerError, ApiProblems.InternalError, Path)).GetRawText();
        foreach (var leak in new[] { "balance", "column", "SELECT", "42703", PostgresFixture.Password, "Npgsql" })
        {
            Assert.DoesNotContain(leak, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task W11_ThirdReadOverTheTestLimitReturns429WithRetryAfter()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await using var factory = ApiFactory.Create(database.ConnectionString, new Dictionary<string, string> { ["RateLimiting:Reads:PermitLimit"] = "2" });
        using var client = factory.CreateClient();

        using var first = await client.GetAsync(Path);
        using var second = await client.GetAsync(Path);
        using var third = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await ProblemAssert.HasProblemAsync(third, HttpStatusCode.TooManyRequests, ApiProblems.RateLimitExceeded, Path);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("errors").ValueKind);
        var retryAfter = int.Parse(Assert.Single(third.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 60);
    }

    [Fact]
    public async Task W12_ReadDoesNotWriteAndReflectsPersistedValues()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await database.ExecuteAsync($"UPDATE wallets SET balance = 12.34 WHERE id = '{DemoData.WalletId}';");
        const string versions = "SELECT string_agg(xmin::text || ':' || updated_at::text, ',') FROM wallets";
        var before = await database.ScalarAsync<string>(versions);
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(1234L, await BalanceCentsAsync(client, Path));
        Assert.Equal(1234L, await BalanceCentsAsync(client, Path));

        Assert.Equal(before, await database.ScalarAsync<string>(versions));
        Assert.Equal("12.34", (await database.RowsAsync("SELECT balance FROM wallets")).Single());
    }

    private static async Task<long> BalanceCentsAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("balanceCents").GetInt64();
    }
}
