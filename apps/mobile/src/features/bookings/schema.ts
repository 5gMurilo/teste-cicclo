import { z } from 'zod';

// Contract: BookingResponse in apps/api, shared by GET /bookings (as page items) and GET /bookings/{id}.
// `status` stays an open string on purpose: a new server-side status must surface as "not recognized"
// (see status.ts) instead of hiding every booking behind a validation error.
const itemSchema = z.object({
  itemId: z.uuid(),
  serviceCode: z.string().min(1),
  unitPriceCents: z.number().int().min(1).max(999_999_999_999),
  status: z.string().min(1),
  machineId: z.uuid().nullable(),
  queuePosition: z.number().int().min(1).nullable(),
  expiresAt: z.iso.datetime().nullable(),
});

export const bookingSchema = z.object({
  bookingId: z.uuid(),
  currency: z.literal('BRL'),
  chargedAmountCents: z.number().int().min(1).max(999_999_999_999),
  remainingBalanceCents: z.number().int().min(0).max(999_999_999_999),
  createdAt: z.iso.datetime(),
  items: z.array(itemSchema).min(1),
});

export type Booking = z.infer<typeof bookingSchema>;
export type BookingItem = Booking['items'][number];
