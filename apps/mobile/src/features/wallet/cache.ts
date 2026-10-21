import type { QueryClient } from '@tanstack/react-query';

import { walletKey, type Wallet } from './queries';

/**
 * Writes a balance the API itself just confirmed (a purchase or a refund response) into the wallet cache.
 * Cancelling first matters: it reverts in-flight reads to their previous data, so a GET that started before
 * the operation cannot land afterwards and bring the old balance back. Only an existing complete wallet is
 * updated; `updatedAt` is not ours to invent, the refetch restores it. The refetch is started but not awaited:
 * the caller's result must not wait for a read.
 */
export async function applyConfirmedBalance(queryClient: QueryClient, balanceCents: number): Promise<void> {
  await queryClient.cancelQueries({ queryKey: walletKey });
  queryClient.setQueryData<Wallet>(walletKey, (current) => (current ? { ...current, balanceCents } : undefined));
  void queryClient.invalidateQueries({ queryKey: walletKey });
}
