import { useQueryClient } from '@tanstack/react-query';
import { useCallback } from 'react';
import { z } from 'zod';

import { capabilities } from '../../shared/api/capabilities';
import { applyConfirmedBalance } from '../wallet/cache';
import { postItemCommand, useItemCommand } from './itemCommand';
import { bookingItemSchema } from './schema';
import { isCancellableStatus } from './status';

// Contract: CancelItemResponse. The refund is whatever the API says it credited; nothing is computed here.
const cancelResponseSchema = z.object({
  item: bookingItemSchema,
  refundedAmountCents: z.number().int().min(0).max(999_999_999_999),
  remainingBalanceCents: z.number().int().min(0).max(999_999_999_999),
});

export type CancelResult = z.infer<typeof cancelResponseSchema>;

export const parseCancelResponse = (json: unknown): CancelResult => cancelResponseSchema.parse(json);

export function canCancel(status: string): boolean {
  return capabilities.itemCommands.cancel && isCancellableStatus(status);
}

export function useCancelItem(bookingId: string) {
  const queryClient = useQueryClient();
  const send = useCallback(
    async (itemId: string): Promise<CancelResult> => {
      const result = parseCancelResponse(await postItemCommand({ bookingId, itemId, action: 'cancel' }));
      // Only a valid authoritative answer touches the wallet cache; a failure never credits anything.
      // A cache problem cannot turn an already confirmed cancellation into a failure.
      try {
        await applyConfirmedBalance(queryClient, result.remainingBalanceCents);
      } catch {
        // The next wallet read catches up.
      }
      return result;
    },
    [bookingId, queryClient],
  );
  return useItemCommand(send);
}
