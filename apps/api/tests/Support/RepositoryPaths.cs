namespace Cicclo.Api.Tests.Support;

/// <summary>Localiza arquivos versionados de `apps/api` a partir da saída dos testes.</summary>
public static class RepositoryPaths
{
    public static string ApiRoot { get; } = FindApiRoot();

    public static string Api(params string[] segments) => Path.Combine([ApiRoot, .. segments]);

    private static string FindApiRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cicclo.Api.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Cicclo.Api.slnx não encontrado acima da saída dos testes.");
    }
}
