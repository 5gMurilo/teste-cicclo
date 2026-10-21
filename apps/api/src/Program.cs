using Cicclo.Api.Platform;
using Cicclo.Api.Platform.Health;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Platform.Observability;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Services.GetCatalog;
using Cicclo.Api.Wallet.GetBalance;
using Microsoft.EntityFrameworkCore;

var bootstrapDemo = BootstrapDemoCommand.IsRequested(args);
var builder = WebApplication.CreateBuilder(BootstrapDemoCommand.WithoutOption(args));
builder.AddApiTelemetry();
builder.Services.AddApiHealthChecks();
builder.Services.AddHttpBoundary();
builder.Services.AddApiOpenApi();
builder.Services.AddApiReadRateLimit(builder.Configuration);
builder.Services.AddOptions<LaundryOptions>()
    .Bind(builder.Configuration.GetSection(LaundryOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddDbContext<CiccloDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString(CiccloDbContext.ConnectionStringName)));
builder.Services.AddScoped<PostgresWalletBalanceQuery>();
builder.Services.AddScoped<PostgresServiceCatalogQuery>();

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

var app = builder.Build();
app.UseHttpBoundary();
app.UseRateLimiter();
app.MapApiOpenApi();
app.MapHealthEndpoints();
app.MapGroup("/api/v1").RequireRateLimiting(ApiReadRateLimit.Policy).MapWalletEndpoint().MapServicesEndpoint();
app.Run();
return 0;

public partial class Program { }
