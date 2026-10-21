import type { QueryClient } from '@tanstack/react-query';

import { bookingsKey } from '../bookings/queryKeys';
import { applyConfirmedBalance } from '../wallet/cache';
import type { ConfirmedBooking } from './createBooking';

/**
 * Brings the read cache in line with a confirmed purchase. Never throws and never re-sends the purchase:
 * the booking is already confirmed, so a cache problem only means the next read has to catch up.
 */
export async function applyConfirmedBooking(queryClient: QueryClient, booking: ConfirmedBooking): Promise<void> {
  try {
    await Promise.all([
      applyConfirmedBalance(queryClient, booking.remainingBalanceCents),
      queryClient.invalidateQueries({ queryKey: bookingsKey }),
    ]);
  } catch {
    // Intentionally swallowed: see the contract above.
  }
}
