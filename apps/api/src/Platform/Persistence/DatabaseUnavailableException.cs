namespace Cicclo.Api.Platform.Persistence;

/// <summary>Falha conhecida de dependência: conexão ausente ou base demo incompleta. Traduzida para 503 na borda HTTP.</summary>
public sealed class DatabaseUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
