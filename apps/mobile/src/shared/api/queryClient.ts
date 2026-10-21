import { QueryClient } from '@tanstack/react-query';

import { ApiError } from './apiError';
import { TransportError } from './client';

const MAX_READ_RETRIES = 1;
const READ_RETRY_DELAY_MS = 1000;

/**
 * Reads retry at most once, and only for transient failures. 4xx (including 429, whose
 * Retry-After is a manual-retry signal), contract errors and invalid bodies are never retried.
 */
export function shouldRetryRead(failureCount: number, error: unknown): boolean {
  if (failureCount >= MAX_READ_RETRIES) return false;
  if (error instanceof TransportError) return error.kind === 'network' || error.kind === 'timeout';
  if (error instanceof ApiError) return error.status >= 500;
  return false;
}

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: shouldRetryRead, retryDelay: READ_RETRY_DELAY_MS },
    },
  });
}

export const queryClient = createQueryClient();
