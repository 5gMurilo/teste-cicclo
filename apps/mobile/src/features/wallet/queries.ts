import { queryOptions } from '@tanstack/react-query';
import { z } from 'zod';

import { requestJson } from '../../shared/api/client';

// Contract: GET /api/v1/wallet (WalletResponse in apps/api, see docs/api-contract.md).
const walletSchema = z.object({
  balanceCents: z.number().int().min(0).max(999_999_999_999),
  currency: z.literal('BRL'),
  updatedAt: z.iso.datetime(),
});

export type Wallet = z.infer<typeof walletSchema>;

export const walletKey = ['wallet'] as const;

const WALLET_STALE_TIME_MS = 10_000;

async function fetchWallet(signal: AbortSignal): Promise<Wallet> {
  return walletSchema.parse(await requestJson({ method: 'GET', path: '/wallet', signal }));
}

export function walletOptions() {
  return queryOptions({
    queryKey: walletKey,
    queryFn: ({ signal }) => fetchWallet(signal),
    staleTime: WALLET_STALE_TIME_MS,
  });
}
