import { queryOptions } from '@tanstack/react-query';
import { z } from 'zod';

import { requestJson } from '../../shared/api/client';
import { bookingsKey } from './queryKeys';
import { bookingSchema, type Booking } from './schema';

// Contract: GET /api/v1/bookings/{bookingId} (BookingResponse in apps/api, see docs/api-contract.md).
const DETAIL_STALE_TIME_MS = 5_000;

export const bookingDetailKey = (bookingId: string) => [...bookingsKey, 'detail', bookingId] as const;

export function isBookingId(value: string): boolean {
  return z.uuid().safeParse(value).success;
}

export class InvalidBookingIdError extends Error {
  constructor() {
    super('Invalid booking id');
    this.name = 'InvalidBookingIdError';
  }
}

async function fetchBooking(bookingId: string, signal: AbortSignal): Promise<Booking> {
  // A malformed id never reaches the network.
  if (!isBookingId(bookingId)) throw new InvalidBookingIdError();
  return bookingSchema.parse(await requestJson({ method: 'GET', path: `/bookings/${bookingId}`, signal }));
}

export function bookingDetailOptions(bookingId: string) {
  return queryOptions({
    queryKey: bookingDetailKey(bookingId),
    queryFn: ({ signal }) => fetchBooking(bookingId, signal),
    enabled: isBookingId(bookingId),
    staleTime: DETAIL_STALE_TIME_MS,
    retry: (failureCount, error) => !(error instanceof InvalidBookingIdError) && failureCount < 1,
  });
}
