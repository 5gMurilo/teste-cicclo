import { useCallback } from 'react';

import { capabilities } from '../../shared/api/capabilities';
import { parseItemResponse, postItemCommand, useItemCommand } from './itemCommand';

/** Activate only applies to a paid item that is waiting; every other status offers no action. */
export function canActivate(status: string): boolean {
  return capabilities.itemCommands.activate && status === 'pending_activation';
}

export function useActivateItem(bookingId: string) {
  const send = useCallback(
    async (itemId: string) => parseItemResponse(await postItemCommand({ bookingId, itemId, action: 'activate' })),
    [bookingId],
  );
  return useItemCommand(send);
}
