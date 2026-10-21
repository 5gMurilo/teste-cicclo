import type { QueryClient } from '@tanstack/react-query';

import { bookingsKey } from '../bookings/queryKeys';
import { walletKey, type Wallet } from '../wallet/queries';
import type { ConfirmedBooking } from './createBooking';

/**
 * Brings the read cache in line with a confirmed purchase. Never throws and never re-sends the purchase:
 * the booking is already confirmed, so a cache problem only means the next read has to catch up.
 */
export async function applyConfirmedBooking(queryClient: QueryClient, booking: ConfirmedBooking): Promise<void> {
  try {
    // Cancel before writing: cancelling reverts a query to its previous data, and a GET that started
    // before the purchase must not land afterwards and bring the old balance back.
    await queryClient.cancelQueries({ queryKey: walletKey });

    // Only an existing, complete wallet is updated; updatedAt is not ours to invent, the refetch restores it.
    queryClient.setQueryData<Wallet>(walletKey, (current) =>
      current ? { ...current, balanceCents: booking.remainingBalanceCents } : undefined,
    );

    await Promise.all([
      queryClient.invalidateQueries({ queryKey: walletKey }),
      queryClient.invalidateQueries({ queryKey: bookingsKey }),
    ]);
  } catch {
    // Intentionally swallowed: see the contract above.
  }
}
