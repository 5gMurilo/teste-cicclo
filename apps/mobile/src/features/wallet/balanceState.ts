import { isConfigError } from '../../shared/config/env';
import type { BalanceState } from '../../shared/layout/AppHeader';
import type { Wallet } from './queries';

type WalletQueryView = {
  data: Wallet | undefined;
  isPending: boolean;
  isError: boolean;
  error?: unknown;
  /** The app is offline or still re-reading after a return: a held balance is only a last known value. */
  retained?: boolean;
};

const timeLabel = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit' });

/** Maps the authoritative wallet query to the header's display state; no default balance, ever. */
export function toBalanceState({ data, isPending, isError, error, retained = false }: WalletQueryView): BalanceState {
  if (data && (isError || retained)) {
    return {
      status: 'stale',
      cents: data.balanceCents,
      updatedAtLabel: `atualizado às ${timeLabel.format(new Date(data.updatedAt))}`,
    };
  }
  if (data) return { status: 'ready', cents: data.balanceCents };
  if (isPending) return { status: 'loading' };
  return isConfigError(error) ? { status: 'error', config: true } : { status: 'error' };
}
