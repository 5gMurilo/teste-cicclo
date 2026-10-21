import { z } from 'zod';

import { requestJson } from '../../shared/api/client';

// Contract: POST /api/v1/bookings (CreateBookingRequest and BookingResponse in apps/api, see docs/api-contract.md).
const bookingResponseSchema = z.object({
  bookingId: z.uuid(),
  currency: z.literal('BRL'),
  chargedAmountCents: z.number().int().min(1).max(999_999_999_999),
  remainingBalanceCents: z.number().int().min(0).max(999_999_999_999),
  createdAt: z.iso.datetime(),
  items: z.array(
    z.object({
      itemId: z.uuid(),
      serviceCode: z.string().min(1),
      unitPriceCents: z.number().int().min(1).max(999_999_999_999),
      status: z.enum([
        'pending_activation',
        'queued',
        'to_be_confirmed',
        'in_progress',
        'cancelled',
        'completed',
        'failed',
      ]),
      machineId: z.uuid().nullable(),
      queuePosition: z.number().int().min(1).nullable(),
      expiresAt: z.iso.datetime().nullable(),
    }),
  ),
});

export type ConfirmedBooking = z.infer<typeof bookingResponseSchema>;

export type BookingRequest = {
  /** One entry per occurrence; the server computes the price, the client only states what it consented to. */
  serviceCodes: readonly string[];
  expectedTotalCents: number;
  preferredFirstServiceCode?: string | null;
};

/** The exact bytes sent on every attempt of one intent, so a replay is byte-identical. */
export function serializeBookingBody(request: BookingRequest): string {
  return JSON.stringify({
    serviceCodes: request.serviceCodes,
    expectedTotalCents: request.expectedTotalCents,
    ...(request.preferredFirstServiceCode ? { preferredFirstServiceCode: request.preferredFirstServiceCode } : {}),
  });
}

/**
 * Sends the purchase. 201 (created) and 200 (replay of the same key) both resolve to the confirmed booking.
 * Rejects with ApiError, TransportError, or ZodError when a success body does not match the contract;
 * deciding what each rejection means financially belongs to the caller.
 */
export async function createBooking(input: {
  idempotencyKey: string;
  body: string;
  signal?: AbortSignal;
}): Promise<ConfirmedBooking> {
  const json = await requestJson({
    method: 'POST',
    path: '/bookings',
    body: input.body,
    headers: { 'Idempotency-Key': input.idempotencyKey },
    signal: input.signal,
  });
  return bookingResponseSchema.parse(json);
}
