import type { BalanceState } from '../../shared/layout/AppHeader';
import type { Wallet } from './queries';

type WalletQueryView = {
  data: Wallet | undefined;
  isPending: boolean;
  isError: boolean;
};

const timeLabel = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit' });

/** Maps the authoritative wallet query to the header's display state; no default balance, ever. */
export function toBalanceState({ data, isPending, isError }: WalletQueryView): BalanceState {
  if (data && isError) {
    return {
      status: 'stale',
      cents: data.balanceCents,
      updatedAtLabel: `atualizado às ${timeLabel.format(new Date(data.updatedAt))}`,
    };
  }
  if (data) return { status: 'ready', cents: data.balanceCents };
  if (isPending) return { status: 'loading' };
  return { status: 'error' };
}
