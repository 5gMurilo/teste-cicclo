import { QueryClient } from '@tanstack/react-query';

/** Isolated cache with retries disabled so a failing fake fetch settles immediately. */
export function createTestQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity } },
  });
}
