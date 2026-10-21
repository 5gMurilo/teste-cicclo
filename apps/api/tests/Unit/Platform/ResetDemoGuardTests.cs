using Cicclo.Api.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Platform;

[Trait("Increment", "E07")]
[Trait("Task", "T1")]
public sealed class ResetDemoGuardTests
{
    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=cicclo", true, "localhost:5432/cicclo")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=cicclo", true, "127.0.0.1:55432/cicclo")]
    [InlineData("Host=::1;Database=cicclo", true, "[::1]:5432/cicclo")]
    [InlineData("Host=postgres;Port=5432;Database=cicclo;Username=cicclo;Password=nao-vazar", true, "postgres:5432/cicclo")]
    [InlineData("Host=LOCALHOST;Database=cicclo", true, "LOCALHOST:5432/cicclo")]
    [InlineData("Host=db.example.com;Database=cicclo", false, "db.example.com:5432/cicclo")]
    [InlineData("Host=10.0.0.5;Database=cicclo", false, "10.0.0.5:5432/cicclo")]
    [InlineData("Host=localhost,db.example.com;Database=cicclo", false, "localhost,db.example.com:5432/cicclo")]
    [InlineData("Host=localhost;Database=postgres", false, "localhost:5432/postgres")]
    [InlineData("Host=localhost;Database=Cicclo", false, "localhost:5432/Cicclo")]
    [InlineData("Host=localhost", false, "localhost:5432/")]
    public void TargetMustBeTheLocalCiccloDatabaseAndIsDescribedWithoutCredentials(string connectionString, bool allowed, string described)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);

        Assert.Equal(allowed, ResetDemoCommand.IsAllowedTarget(target));
        Assert.Equal(described, ResetDemoCommand.Describe(target));
    }

    [Theory]
    [InlineData(new[] { "--reset-demo", "--confirm-reset", "localhost:5432/cicclo" }, "localhost:5432/cicclo", new string[0])]
    [InlineData(new[] { "--urls", "x", "--reset-demo" }, null, new[] { "--urls", "x" })]
    [InlineData(new[] { "--reset-demo", "--confirm-reset" }, null, new string[0])]
    public void ArgumentsCarryOnlyTheConfirmationAndNeverReachHostConfiguration(string[] args, string? confirmation, string[] remaining)
    {
        Assert.True(ResetDemoCommand.IsRequested(args));
        Assert.Equal(confirmation, ResetDemoCommand.Confirmation(args));
        Assert.Equal(remaining, ResetDemoCommand.WithoutOptions(args));
    }

    [Fact]
    public void ExplicitTableSetIsExactlyTheApplicationModel()
    {
        using var context = new CiccloDbContext(new DbContextOptionsBuilder<CiccloDbContext>().UseNpgsql().Options);

        var modelTables = context.GetService<IDesignTimeModel>().Model.GetEntityTypes().Select(entity => entity.GetTableName()!);

        Assert.Equal(modelTables.Order(StringComparer.Ordinal), ResetDemoCommand.ApplicationTables.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("__EFMigrationsHistory", ResetDemoCommand.ApplicationTables);
    }

    [Theory]
    [InlineData("Host=postgres;Database=cicclo;Username=cicclo;Password=nao-vazar", "cicclo.api")]
    [InlineData("Host=postgres;Database=cicclo;Application Name=", "cicclo.api")]
    [InlineData("Host=postgres;Database=cicclo;Application Name=outro", "outro")]
    public void ApiConnectionsAreIdentifiedUnlessAnotherNameIsConfigured(string configured, string expected)
    {
        using var connection = new NpgsqlConnection(configured);

        ApiConnectionIdentity.Identify(connection);

        var identified = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal(expected, identified.ApplicationName);
        Assert.Equal(("postgres", "cicclo"), (identified.Host, identified.Database));
        Assert.Equal(new NpgsqlConnectionStringBuilder(configured).Password, identified.Password);
    }
}
