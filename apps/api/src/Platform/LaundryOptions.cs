using System.ComponentModel.DataAnnotations;

namespace Cicclo.Api.Platform;

/// <summary>Limites operacionais aprovados; valores inválidos impedem o startup (ValidateOnStart).</summary>
public sealed class LaundryOptions
{
    public const string SectionName = "Laundry";

    /// <summary>Habilita somente preparos locais da demonstração; nunca é presumido.</summary>
    public bool DemoMode { get; init; }

    [Range(1, int.MaxValue)]
    public int ConfirmationTimeoutSeconds { get; init; } = 60;

    [Range(1, int.MaxValue)]
    public int QueueWorkerIntervalSeconds { get; init; } = 5;

    [Range(1, 10)]
    public int MaxServicesPerBooking { get; init; } = 10;
}
