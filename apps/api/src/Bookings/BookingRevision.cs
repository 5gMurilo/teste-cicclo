namespace Cicclo.Api.Bookings;

/// <summary>
/// Revisão persistida do booking (D09/D10): compara avisos do mesmo booking sem usar timestamp nem replay como ordem.
/// Um booking nasce na revisão inicial; cada transação confirmada que altera dados persistidos do booking ou de seus
/// itens incrementa uma vez por booking distinto, na mesma transação da mutação. Leitura, replay puro, rollback, posição
/// derivada da fila, relato e ocupação sintética não incrementam. Não é campo dos DTOs REST, que seguem autoritativos.
/// </summary>
public static class BookingRevision
{
    /// <summary>Revisão de uma compra nova e, pelo backfill da migration, de todo booking anterior à E09.</summary>
    public const long Initial = 1;
}
