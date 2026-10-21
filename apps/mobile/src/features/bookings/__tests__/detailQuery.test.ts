import { QueryObserver, type QueryClient } from '@tanstack/react-query';

import { ApiError } from '../../../shared/api/apiError';
import { createTestQueryClient } from '../../../test/queryClient';
import { bookingDetailKey, bookingDetailOptions, InvalidBookingIdError, isBookingId } from '../detailQuery';

const ID = '00000000-0000-4000-8000-000000000001';
const BOOKING = {
  bookingId: ID,
  currency: 'BRL',
  chargedAmountCents: 1500,
  remainingBalanceCents: 3500,
  createdAt: '2026-10-21T12:00:00Z',
  items: [
    {
      itemId: '10000000-0000-4000-8000-000000000001',
      serviceCode: 'wash',
      unitPriceCents: 1500,
      status: 'to_be_confirmed',
      machineId: '20000000-0000-4000-8000-000000000001',
      queuePosition: null,
      expiresAt: '2026-10-21T12:01:00Z',
    },
  ],
};

let client: QueryClient;
let calls: string[];
let reply: { status: number; body: unknown };

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  client = createTestQueryClient();
  calls = [];
  reply = { status: 200, body: BOOKING };
  globalThis.fetch = jest.fn(async (url: string | URL | Request) => {
    calls.push(String(url));
    return {
      status: reply.status,
      ok: reply.status < 300,
      headers: { get: () => null },
      text: async () => JSON.stringify(reply.body),
    } as unknown as Response;
  }) as unknown as typeof fetch;
});

afterEach(() => client.clear());

describe('bookingDetailOptions', () => {
  it('returns the booking with the historical price from the API, not from any catalog', async () => {
    client.setQueryData(['services'], { services: [{ code: 'wash', unitPriceCents: 1890 }] });

    const booking = await client.fetchQuery(bookingDetailOptions(ID));

    expect(calls).toEqual([`http://api.test:5001/api/v1/bookings/${ID}`]);
    expect(booking.items[0]).toMatchObject({ unitPriceCents: 1500, status: 'to_be_confirmed', expiresAt: '2026-10-21T12:01:00Z' });
    expect(client.getQueryData(bookingDetailKey(ID))).toEqual(booking);
  });

  it.each(['abc', '', '123', `${ID}/extra`, '../wallet'])('does not send a request for the malformed id %p', async (id) => {
    expect(isBookingId(id)).toBe(false);
    await expect(client.fetchQuery(bookingDetailOptions(id))).rejects.toBeInstanceOf(InvalidBookingIdError);

    const observer = new QueryObserver(client, bookingDetailOptions(id));
    const unsubscribe = observer.subscribe(() => undefined);
    unsubscribe();

    expect(calls).toHaveLength(0);
    expect(observer.getCurrentResult().fetchStatus).toBe('idle');
  });

  it('reports a missing booking as a recoverable ApiError 404', async () => {
    reply = { status: 404, body: { errorCode: 'BOOKING_NOT_FOUND' } };

    await expect(client.fetchQuery(bookingDetailOptions(ID))).rejects.toMatchObject({
      status: 404,
      errorCode: 'BOOKING_NOT_FOUND',
    });
    await expect(client.fetchQuery(bookingDetailOptions(ID))).rejects.toBeInstanceOf(ApiError);
  });

  it.each([
    ['a body without items', { ...BOOKING, items: undefined }],
    ['an item price as string', { ...BOOKING, items: [{ ...BOOKING.items[0], unitPriceCents: '1500' }] }],
    ['an HTML-like payload', 'not-an-object'],
  ])('rejects %s', async (_name, body) => {
    reply = { status: 200, body };

    await expect(client.fetchQuery(bookingDetailOptions(ID))).rejects.toThrow();
  });
});
