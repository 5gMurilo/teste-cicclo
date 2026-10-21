namespace Cicclo.Api.Bookings;

/// <summary>Motivos estáveis de recusa de uma compra; a borda HTTP traduz cada um para seu errorCode.</summary>
public enum BookingRejection
{
    /// <summary>VALIDATION_ERROR: estrutura inválida, como total esperado não positivo ou código nulo.</summary>
    ValidationError,

    /// <summary>INVALID_SERVICE_SELECTION: lote vazio, acima do limite ou preferência fora do lote.</summary>
    InvalidServiceSelection,

    /// <summary>SERVICE_NOT_FOUND: algum código não existe no catálogo.</summary>
    ServiceNotFound,

    /// <summary>SERVICE_DISABLED: algum serviço do lote está inativo.</summary>
    ServiceDisabled,

    /// <summary>NO_OPERATIONAL_MACHINE: algum serviço do lote não tem máquina operacional.</summary>
    NoOperationalMachine,

    /// <summary>PRICE_CHANGED: o total esperado pelo cliente difere do total calculado no servidor.</summary>
    PriceChanged,

    /// <summary>WALLET_INSUFFICIENT_BALANCE: o débito condicional não encontrou saldo suficiente.</summary>
    InsufficientBalance,

    /// <summary>IDEMPOTENCY_CONFLICT: a chave já confirmou uma intenção com outro fingerprint.</summary>
    IdempotencyConflict,
}
