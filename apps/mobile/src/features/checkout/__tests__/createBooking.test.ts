import { ZodError } from 'zod';

import { createBooking, serializeBookingBody } from '../createBooking';

const KEY = '3f2b8c1e-5d4a-4f6b-9a7c-1e2d3c4b5a69';
const BOOKING = {
  bookingId: '11111111-1111-4111-8111-111111111111',
  currency: 'BRL',
  chargedAmountCents: 1890,
  remainingBalanceCents: 3110,
  createdAt: '2026-10-21T12:00:00Z',
  items: [
    {
      itemId: '22222222-2222-4222-8222-222222222222',
      serviceCode: 'wash',
      unitPriceCents: 1890,
      status: 'queued',
      machineId: null,
      queuePosition: 1,
      expiresAt: null,
    },
  ],
};

type Sent = { url: string; init: RequestInit };
let sent: Sent[];

function respond(status: number, body: unknown) {
  globalThis.fetch = jest.fn(async (url: string | URL | Request, init?: RequestInit) => {
    sent.push({ url: String(url), init: init as RequestInit });
    return {
      status,
      ok: status >= 200 && status < 300,
      headers: { get: () => null },
      text: async () => JSON.stringify(body),
    } as unknown as Response;
  }) as unknown as typeof fetch;
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  sent = [];
});

describe('serializeBookingBody', () => {
  it('serializes duplicates as separate entries and the expected total, with no price or balance fields', () => {
    const body = JSON.parse(serializeBookingBody({ serviceCodes: ['wash', 'wash'], expectedTotalCents: 3780 }));

    expect(body).toEqual({ serviceCodes: ['wash', 'wash'], expectedTotalCents: 3780 });
  });

  it('includes the preference when present and omits it when null or absent', () => {
    const base = { serviceCodes: ['wash', 'dry'], expectedTotalCents: 3980 };

    expect(JSON.parse(serializeBookingBody({ ...base, preferredFirstServiceCode: 'dry' }))).toEqual({
      ...base,
      preferredFirstServiceCode: 'dry',
    });
    expect(JSON.parse(serializeBookingBody({ ...base, preferredFirstServiceCode: null }))).toEqual(base);
    expect(JSON.parse(serializeBookingBody(base))).toEqual(base);
  });
});

describe('createBooking', () => {
  it('POSTs the given body and Idempotency-Key unchanged and returns the confirmed booking on 201', async () => {
    respond(201, BOOKING);
    const body = serializeBookingBody({ serviceCodes: ['wash'], expectedTotalCents: 1890 });

    const booking = await createBooking({ idempotencyKey: KEY, body });

    expect(booking).toEqual(BOOKING);
    expect(sent).toHaveLength(1);
    expect(sent[0].url).toBe('http://api.test:5001/api/v1/bookings');
    expect(sent[0].init.method).toBe('POST');
    expect(sent[0].init.body).toBe(body);
    expect(new Headers(sent[0].init.headers).get('idempotency-key')).toBe(KEY);
  });

  it('treats a 200 replay as the same confirmed booking, with the current balance from the response', async () => {
    respond(200, { ...BOOKING, remainingBalanceCents: 1020 });

    const booking = await createBooking({ idempotencyKey: KEY, body: '{}' });

    expect(booking.bookingId).toBe(BOOKING.bookingId);
    expect(booking.remainingBalanceCents).toBe(1020);
  });

  it.each([
    ['missing items', { ...BOOKING, items: undefined }],
    ['balance as string', { ...BOOKING, remainingBalanceCents: '3110' }],
    ['negative balance', { ...BOOKING, remainingBalanceCents: -1 }],
    ['unknown item status', { ...BOOKING, items: [{ ...BOOKING.items[0], status: 'teleported' }] }],
    ['non-UUID booking id', { ...BOOKING, bookingId: 'abc' }],
  ])('rejects a 2xx body with %s instead of inventing a booking', async (_name, body) => {
    respond(201, body);

    await expect(createBooking({ idempotencyKey: KEY, body: '{}' })).rejects.toBeInstanceOf(ZodError);
  });
});
