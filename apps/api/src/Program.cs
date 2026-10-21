using Cicclo.Api.Platform;
using Cicclo.Api.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

var bootstrapDemo = BootstrapDemoCommand.IsRequested(args);
var builder = WebApplication.CreateBuilder(BootstrapDemoCommand.WithoutOption(args));
builder.Services.AddHealthChecks();
builder.Services.Configure<LaundryOptions>(builder.Configuration.GetSection(LaundryOptions.SectionName));
builder.Services.AddDbContext<CiccloDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString(CiccloDbContext.ConnectionStringName)));

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
app.MapHealthChecks("/health");
app.Run();
return 0;

public partial class Program { }
