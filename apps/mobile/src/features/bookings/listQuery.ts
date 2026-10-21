import { infiniteQueryOptions } from '@tanstack/react-query';
import { z } from 'zod';

import { requestJson } from '../../shared/api/client';
import { bookingsKey } from './queryKeys';
import { bookingSchema, type Booking } from './schema';

// Contract: GET /api/v1/bookings?page=&pageSize= (PageResponse<BookingResponse> in apps/api).
const pageSchema = z.object({
  items: z.array(bookingSchema),
  page: z.number().int().min(1),
  pageSize: z.number().int().min(1).max(100),
  hasNextPage: z.boolean(),
});

export type BookingsPage = z.infer<typeof pageSchema>;

export const DEFAULT_PAGE_SIZE = 20;
const LIST_STALE_TIME_MS = 10_000;

export const bookingsListKey = (pageSize: number) => [...bookingsKey, 'list', { pageSize }] as const;

async function fetchPage(page: number, pageSize: number, signal: AbortSignal): Promise<BookingsPage> {
  return pageSchema.parse(
    await requestJson({ method: 'GET', path: `/bookings?page=${page}&pageSize=${pageSize}`, signal }),
  );
}

export type BookingsList = {
  /** Loaded bookings only, newest first; more may exist while `hasMore` is true. */
  bookings: Booking[];
  hasMore: boolean;
};

function newestFirst(a: Booking, b: Booking): number {
  return b.createdAt.localeCompare(a.createdAt) || b.bookingId.localeCompare(a.bookingId);
}

export function bookingsListOptions(pageSize: number = DEFAULT_PAGE_SIZE) {
  return infiniteQueryOptions({
    queryKey: bookingsListKey(pageSize),
    queryFn: ({ pageParam, signal }) => fetchPage(pageParam, pageSize, signal),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.hasNextPage ? last.page + 1 : undefined),
    staleTime: LIST_STALE_TIME_MS,
    select: (data): BookingsList => ({
      bookings: data.pages.flatMap((p) => p.items).sort(newestFirst),
      hasMore: data.pages[data.pages.length - 1]?.hasNextPage ?? false,
    }),
  });
}
