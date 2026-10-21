using System.Reflection;
using System.Text.Json;
using Cicclo.Api.Bookings;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Laundry;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Services;
using Cicclo.Api.Services.GetCatalog;
using Cicclo.Api.Tests.Support;
using Cicclo.Api.Wallet;
using Cicclo.Api.Wallet.GetBalance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CreateBookingUseCase = Cicclo.Api.Bookings.CreateBooking.CreateBooking;

namespace Cicclo.Api.Tests.Unit.Architecture;

[Trait("Increment", "E01")]
[Trait("Task", "T1")]
public sealed class PersistenceBoundaryTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] DomainNamespaces = ["Cicclo.Api.Wallet", "Cicclo.Api.Services", "Cicclo.Api.Laundry", "Cicclo.Api.Bookings"];

    private static readonly string[] ForbiddenPrefixes = ["Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "System.Linq.IQueryable"];

    /// <summary>
    /// Adaptadores HTTP/PostgreSQL dos casos de uso (ENG-04), únicos tipos das capacidades que podem usar
    /// ASP.NET/EF/Npgsql. Todo o resto dos namespaces e sub-namespaces de capacidade é regra/porta/DTO.
    /// </summary>
    private static readonly Type[] FrameworkAdapters =
    [
        typeof(WalletEndpoint), typeof(PostgresWalletBalanceQuery),
        typeof(ServicesEndpoint), typeof(PostgresServiceCatalogQuery),
        typeof(PostgresCreateBookingSessions), typeof(PostgresLaundrySessions),
        typeof(PostgresBookingProjection), typeof(Cicclo.Api.Bookings.GetBooking.GetBookingEndpoint),
    ];

    [Fact]
    public void DomainModelsDoNotDependOnPersistenceOrHttpFrameworks()
    {
        var domainTypes = typeof(Program).Assembly.GetTypes()
            .Where(type => IsInDomainNamespace(type.Namespace) && !IsCompilerGenerated(type) && !IsFrameworkAdapter(type))
            .ToList();

        Assert.Superset(
            new HashSet<Type>
            {
                typeof(WalletAccount), typeof(LaundryService), typeof(Machine), typeof(MachineType), typeof(MachineState),
                typeof(Booking), typeof(BookingItem), typeof(BookingItemStatus), typeof(LedgerEntry), typeof(LedgerEntryKind),
                typeof(CreateBookingUseCase), typeof(ICreateBookingSessions), typeof(ICreateBookingSession), typeof(StoredBooking),
                typeof(WalletBalance), typeof(ServiceCatalogEntry), typeof(BookingView), typeof(BookingItemView), typeof(BookingResponse),
                typeof(OfferPolicy), typeof(QueueCoordinator), typeof(IQueueSession), typeof(ILaundrySession), typeof(ILaundrySessions),
                typeof(Cicclo.Api.Laundry.ConfirmOffer.ConfirmOffer), typeof(Cicclo.Api.Laundry.ConfirmOffer.ConfirmOfferResult),
                typeof(Cicclo.Api.Laundry.ReconcileOffers.ReconcileOffers), typeof(LaundryMetrics), typeof(QueueChanges),
            },
            domainTypes.ToHashSet());
        var violations = domainTypes
            .SelectMany(type => ReferencedTypes(type).Select(reference => $"{type.Name} -> {reference.FullName}"))
            .Where(reference => ForbiddenPrefixes.Any(prefix => reference.Split(" -> ")[1].StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void ModelMapsOnlyInitialEntitiesWithoutHasDataSeed()
    {
        using var context = new CiccloDbContext(new DbContextOptionsBuilder<CiccloDbContext>().UseNpgsql().Options);

        var entities = context.GetService<IDesignTimeModel>().Model.GetEntityTypes().ToList();

        Assert.Equal(
            ["machines", "service_request_items", "service_requests", "services", "wallet_transactions", "wallets"],
            entities.Select(entity => entity.GetTableName()).Order(StringComparer.Ordinal));
        Assert.All(entities, entity => Assert.Empty(entity.GetSeedData()));
    }

    [Fact]
    public void HostRegistersDbContextPerScopeWithoutConnectingWhenNoConnectionIsConfigured()
    {
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();

        var context = first.ServiceProvider.GetRequiredService<CiccloDbContext>();

        Assert.Same(context, first.ServiceProvider.GetRequiredService<CiccloDbContext>());
        Assert.NotSame(context, second.ServiceProvider.GetRequiredService<CiccloDbContext>());
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void MigrationsUseLocalPinnedToolAndDesignAssetsStayPrivate()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Api(".config", "dotnet-tools.json")));
        var tool = manifest.RootElement.GetProperty("tools").GetProperty("dotnet-ef");
        var project = File.ReadAllText(RepositoryPaths.Api("src", "Cicclo.Api.csproj"));

        Assert.True(manifest.RootElement.GetProperty("isRoot").GetBoolean());
        Assert.Equal("10.0.13", tool.GetProperty("version").GetString());
        Assert.Matches(
            "<PackageReference Include=\"Microsoft.EntityFrameworkCore.Design\" Version=\"10.0.13\">\\s*<IncludeAssets>[^<]*</IncludeAssets>\\s*<PrivateAssets>all</PrivateAssets>",
            project);
    }

    /// <summary>Namespace da capacidade ou qualquer sub-namespace dela (por exemplo Cicclo.Api.Bookings.CreateBooking).</summary>
    private static bool IsInDomainNamespace(string? name) =>
        name is not null && DomainNamespaces.Any(root => name == root || name.StartsWith(root + ".", StringComparison.Ordinal));

    /// <summary>O adaptador ou um tipo aninhado nele (sessão, linhas SQL, lambdas/state machines geradas).</summary>
    private static bool IsFrameworkAdapter(Type type)
    {
        for (Type? current = type; current is not null; current = current.DeclaringType)
        {
            if (FrameworkAdapters.Contains(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCompilerGenerated(Type type) =>
        type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null;

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var types = new List<Type?> { type.BaseType };
        types.AddRange(type.GetInterfaces());
        types.AddRange(type.GetCustomAttributesData().Select(attribute => attribute.AttributeType));
        foreach (var member in type.GetMembers(all))
        {
            types.AddRange(member.GetCustomAttributesData().Select(attribute => attribute.AttributeType));
            switch (member)
            {
                case PropertyInfo property:
                    types.Add(property.PropertyType);
                    break;
                case FieldInfo field:
                    types.Add(field.FieldType);
                    break;
                case MethodBase method:
                    types.AddRange(method.GetParameters().Select(parameter => parameter.ParameterType));
                    if (method is MethodInfo info)
                    {
                        types.Add(info.ReturnType);
                    }

                    break;
            }
        }

        return types.OfType<Type>().SelectMany(Flatten);
    }

    private static IEnumerable<Type> Flatten(Type type) =>
        type.IsGenericType
            ? new[] { type.GetGenericTypeDefinition() }.Concat(type.GetGenericArguments().SelectMany(Flatten))
            : type.HasElementType ? Flatten(type.GetElementType()!) : [type];
}
