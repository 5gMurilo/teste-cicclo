using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cicclo.Api.Tests.Support;

/// <summary>Host HTTP real da API apontando para um banco isolado de teste (ou nenhum, quando a conexão é nula).</summary>
public static class ApiFactory
{
    /// <summary>Endereço onde nenhum PostgreSQL escuta: simula banco fora do ar sem tocar no container compartilhado.</summary>
    public const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=cicclo;Username=cicclo;Password=nao-vazar-down;Timeout=2;Pooling=false";

    public static WebApplicationFactory<Program> Create(string? connectionString, IReadOnlyDictionary<string, string>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            if (connectionString is not null)
            {
                host.UseSetting("ConnectionStrings:Cicclo", connectionString);
            }

            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                host.UseSetting(key, value);
            }
        });
}
