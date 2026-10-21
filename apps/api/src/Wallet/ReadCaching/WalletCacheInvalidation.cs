namespace Cicclo.Api.Wallet.ReadCaching;

/// <summary>
/// Efeito pós-commit das escritas que alteram a wallet (CACHE-04/05): compra criada, cancelamento com refund, falha
/// confirmada com refund (breakdown ou incidente) e reset da demo. É chamado somente depois de o commit ter sido
/// observado e de a sessão (transação e locks) ter sido descartada; nunca regrava saldo, só remove a chave. Usa o
/// orçamento próprio da porta, sem o token da requisição, para que um cliente que desistiu depois do commit não deixe
/// a projeção antiga no cache; qualquer falha é registrada e engolida, sem alterar o resultado já confirmado nem
/// disparar nova tentativa financeira. Sem cache registrado, não faz nada.
/// </summary>
public sealed partial class WalletCacheInvalidation(ILogger<WalletCacheInvalidation> logger, IWalletReadCache? cache = null)
{
    public async Task AfterCommitAsync()
    {
        if (cache is null)
        {
            return;
        }

        try
        {
            await cache.InvalidateAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogFailed(exception.GetType().Name);
        }
    }

    /// <summary>Somente o tipo da exceção: nem mensagem nem configuração do Redis.</summary>
    [LoggerMessage(EventId = 801, Level = LogLevel.Warning, Message = "Invalidação pós-commit do cache da wallet falhou ({ErrorType}); o resultado confirmado no PostgreSQL não muda e a entrada expira pelo TTL.")]
    private partial void LogFailed(string errorType);
}
