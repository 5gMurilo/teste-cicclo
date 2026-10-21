using System.Diagnostics.Metrics;
using System.Text.Json;
using Cicclo.Api.Bookings;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Laundry;
using Cicclo.Api.Platform;
using Cicclo.Api.Wallet.ReadCaching;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cicclo.Api.Tests.Support;

/// <summary>
/// Apoio dos contratos HTTP de bookings: host real sobre um banco isolado com relógio controlado (o worker de
/// reconciliação só anda quando o teste avança o relógio) e compras criadas pelo caso de uso interno.
/// </summary>
public static class BookingApi
{
    public const string FixtureApp = "cicclo-e04-fixture";

    public static WebApplicationFactory<Program> Create(TestDatabase database, TimeProvider clock, IReadOnlyDictionary<string, string>? settings = null) =>
        ApiFactory.Create(database.ConnectionString, settings)
            .WithWebHostBuilder(host => host.ConfigureTestServices(services => services.AddSingleton(clock)));

    /// <summary>Compra pelo núcleo interno (sem HTTP), no instante do relógio e com o preço do seed.</summary>
    public static async Task<BookingSnapshot> PurchaseAsync(TestDatabase database, TimeProvider clock, string[] codes, string? preferred = null)
    {
        var total = codes.Sum(code => code == "wash" ? 1890L : 2090L);
        var intent = BookingIntent.Create(codes, preferred, total, 10).Intent!;
        var result = await UseCase(new PostgresCreateBookingSessions(database.ContextFactory(FixtureApp)), clock)
            .ExecuteAsync(Guid.NewGuid(), intent, CancellationToken.None);
        return result.Booking ?? throw new InvalidOperationException($"Compra de fixture recusada: {result.Outcome} {result.Rejection}.");
    }

    /// <summary>
    /// Caso de uso real de compra com o coordenador de fila (prazo de 60 s) e métricas sobre o mesmo relógio; sem cache
    /// de leitura registrado, a invalidação pós-commit não faz nada (Caching:Enabled=false).
    /// </summary>
    public static CreateBooking UseCase(ICreateBookingSessions sessions, TimeProvider clock, IMeterFactory? meters = null) =>
        new(
            sessions,
            new QueueCoordinator(clock, Options.Create(new LaundryOptions())),
            new LaundryMetrics(meters ?? new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()),
            clock,
            new WalletCacheInvalidation(NullLogger<WalletCacheInvalidation>.Instance),
            NullLogger<CreateBooking>.Instance);

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
