import { useCallback } from 'react';

import { capabilities } from '../../shared/api/capabilities';
import { secondsUntil } from './components/OfferCountdown';
import { parseItemResponse, postItemCommand, useItemCommand } from './itemCommand';
import type { BookingItem } from './schema';

/** A live offer: waiting for confirmation with a server deadline that has not passed on this clock. */
export function hasLiveOffer(item: BookingItem, nowMs: number): boolean {
  return item.status === 'to_be_confirmed' && item.expiresAt !== null && secondsUntil(item.expiresAt, nowMs) > 0;
}

export const confirmEnabled = () => capabilities.itemCommands.confirm;

/**
 * The local clock only decides whether to bother the server: at zero nothing is sent. When time seems left
 * the server still decides (410 wins), and the item is never moved to in_progress locally.
 */
export function useConfirmItem(bookingId: string) {
  const send = useCallback(
    async (itemId: string) => parseItemResponse(await postItemCommand({ bookingId, itemId, action: 'confirm' })),
    [bookingId],
  );
  const command = useItemCommand(send);

  /** Resolves false, sending nothing, when the offer is no longer live on this clock. */
  const confirm = useCallback(
    async (item: BookingItem): Promise<boolean> => {
      if (!hasLiveOffer(item, Date.now())) return false;
      await command.run(item.itemId);
      return true;
    },
    [command],
  );

  return { state: command.state, confirm };
}
