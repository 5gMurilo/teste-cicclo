import { useIsFocused } from 'expo-router';
import { useCallback } from 'react';

import { ApiError } from '../../shared/api/apiError';

export const LIST_POLL_MS = 10_000;
export const DETAIL_POLL_MS = 5_000;

type PollingQuery = { state: { error: unknown } };

/**
 * `refetchInterval` for a bookings query: poll only while the screen is visible, something is not terminal
 * yet, and the server has not just rate limited us. App in background and offline are already handled by
 * Query through the focus and online managers (see useMobileLifecycle). Never polls the wallet or sends a POST.
 */
export function useBookingPolling<TData>(intervalMs: number, hasLiveItems: (data: TData | undefined) => boolean) {
  const focused = useIsFocused();
  return useCallback(
    (query: PollingQuery & { state: { data: TData | undefined } }): number | false => {
      if (!focused) return false;
      if (query.state.error instanceof ApiError && query.state.error.status === 429) return false;
      return hasLiveItems(query.state.data) ? intervalMs : false;
    },
    [focused, hasLiveItems, intervalMs],
  );
}
