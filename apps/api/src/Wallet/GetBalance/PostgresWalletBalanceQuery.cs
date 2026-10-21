using Cicclo.Api.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cicclo.Api.Wallet.GetBalance;

/// <summary>Adaptador de leitura do saldo da wallet demo; resolve a conta no servidor e nunca escreve.</summary>
public sealed class PostgresWalletBalanceQuery(CiccloDbContext db)
{
    public async Task<WalletBalance> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(db.Database.GetConnectionString()))
        {
            throw new DatabaseUnavailableException("Conexão com o PostgreSQL não configurada.");
        }

        return await db.Wallets
                .AsNoTracking()
                .Where(wallet => wallet.Id == DemoWallet.Id)
                .Select(wallet => new WalletBalance(wallet.Balance, wallet.Currency, wallet.UpdatedAt))
                .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DatabaseUnavailableException("Wallet demo ausente: execute o bootstrap.");
    }
}
