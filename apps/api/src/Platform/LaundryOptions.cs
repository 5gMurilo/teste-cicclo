namespace Cicclo.Api.Platform;

public sealed class LaundryOptions
{
    public const string SectionName = "Laundry";

    /// <summary>Habilita somente preparos locais da demonstração; nunca é presumido.</summary>
    public bool DemoMode { get; init; }
}
