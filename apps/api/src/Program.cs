using Cicclo.Api.Bookings;
using Cicclo.Api.Bookings.ActivateItem;
using Cicclo.Api.Bookings.CancelItem;
using Cicclo.Api.Bookings.ConfirmItem;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Bookings.GetBooking;
using Cicclo.Api.Bookings.ListBookings;
using Cicclo.Api.Laundry.ConfirmMachineFailure;
using Cicclo.Api.Laundry.ConfirmOffer;
using Cicclo.Api.Laundry.GetMachines;
using Cicclo.Api.Laundry.ReportIssue;
using Cicclo.Api.Laundry.Simulation;
using Cicclo.Api.Platform;
using Cicclo.Api.Platform.BackgroundServices;
using Cicclo.Api.Platform.Health;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Platform.Observability;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Services.GetCatalog;
using Cicclo.Api.Wallet;
using Cicclo.Api.Wallet.GetBalance;
using Cicclo.Api.Wallet.ListTransactions;
using Microsoft.EntityFrameworkCore;

var bootstrapDemo = BootstrapDemoCommand.IsRequested(args);
var builder = WebApplication.CreateBuilder(BootstrapDemoCommand.WithoutOption(args));
builder.AddApiTelemetry();
builder.Services.AddApiHealthChecks();
builder.Services.AddHttpBoundary();
builder.Services.AddApiOpenApi();
builder.Services.AddApiRateLimits(builder.Configuration);
builder.Services.AddOptions<LaundryOptions>()
    .Bind(builder.Configuration.GetSection(LaundryOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddDbContext<CiccloDbContext>(
    (services, options) =>
        options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString(CiccloDbContext.ConnectionStringName)),
    optionsLifetime: ServiceLifetime.Singleton);
// Casos de uso transacionais abrem um contexto novo por sessão, com as mesmas opções do contexto por escopo.
builder.Services.AddDbContextFactory<CiccloDbContext>();
builder.Services.AddScoped<PostgresWalletBalanceQuery>();
builder.Services.AddScoped<PostgresServiceCatalogQuery>();
builder.Services.AddScoped<PostgresBookingProjection>();
builder.Services.AddScoped<PostgresLedgerQuery>();
builder.Services.AddScoped<PostgresMachinesQuery>();

if (bootstrapDemo)
{
    builder.Services.AddSingleton<DemoSeedSql>();
    builder.Services.AddScoped<BootstrapDemoCommand>();
    using var host = builder.Build();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    using var scope = host.Services.CreateScope();
    return await scope.ServiceProvider.GetRequiredService<BootstrapDemoCommand>().RunAsync(cancellation.Token);
}

builder.Services.AddOfferReconciliation();
builder.Services.AddSingleton<BookingMetrics>();
builder.Services.AddScoped<ICreateBookingSessions, PostgresCreateBookingSessions>();
builder.Services.AddScoped<CreateBooking>();
builder.Services.AddScoped<ActivateItem>();
builder.Services.AddScoped<ConfirmOffer>();
builder.Services.AddSingleton<RefundMetrics>();
builder.Services.AddScoped<CancelItem>();
builder.Services.AddScoped<OccupyMachine>();
builder.Services.AddScoped<CompleteMachine>();
builder.Services.AddScoped<ReportIssue>();
builder.Services.AddScoped<ConfirmMachineFailure>();
var app = builder.Build();
app.UseHttpBoundary();
app.UseRateLimiter();
app.MapApiOpenApi();
app.MapHealthEndpoints();
app.MapGroup("/api/v1").RequireRateLimiting(ApiRateLimits.Reads).MapWalletEndpoint().MapServicesEndpoint();
app.MapGroup("/api/v1").RequireRateLimiting(ApiRateLimits.BookingReads).MapGetBookingEndpoint().MapListBookingsEndpoint().MapListTransactionsEndpoint().MapGetMachinesEndpoint();
app.MapGroup("/api/v1").RequireRateLimiting(ApiRateLimits.BookingCreate).MapCreateBookingEndpoint();
app.MapGroup("/api/v1").RequireRateLimiting(ApiRateLimits.ItemCommands).MapActivateItemEndpoint().MapConfirmItemEndpoint();
app.MapGroup("/api/v1").RequireRateLimiting(ApiRateLimits.ItemCancel).MapCancelItemEndpoint();
app.MapGroup("/api/v1").RequireRateLimiting(ApiRateLimits.IssueReports).MapReportIssueEndpoint();
app.MapSimulationEndpoints();
app.Run();
return 0;

public partial class Program { }
