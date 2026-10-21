using Cicclo.Api.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddDbContext<CiccloDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString(CiccloDbContext.ConnectionStringName)));

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();

public partial class Program { }
