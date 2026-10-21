import { InfiniteQueryObserver, type QueryClient } from '@tanstack/react-query';

import { createTestQueryClient } from '../../../test/queryClient';
import { bookingsListKey, bookingsListOptions } from '../listQuery';

const booking = (n: number, createdAt: string, status = 'queued') => ({
  bookingId: `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  currency: 'BRL',
  chargedAmountCents: 1890,
  remainingBalanceCents: 3110,
  createdAt,
  items: [
    {
      itemId: `10000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
      serviceCode: 'wash',
      unitPriceCents: 1890,
      status,
      machineId: null,
      queuePosition: null,
      expiresAt: null,
    },
  ],
});
const page = (items: unknown[], pageNumber = 1, hasNextPage = false, pageSize = 20) => ({
  items,
  page: pageNumber,
  pageSize,
  hasNextPage,
});

let client: QueryClient;
let requested: { url: string; method: string; signal: AbortSignal | undefined }[];
let answer: (url: string) => { status: number; body: unknown };

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  client = createTestQueryClient();
  requested = [];
  globalThis.fetch = jest.fn(async (url: string | URL | Request, init?: RequestInit) => {
    requested.push({ url: String(url), method: init?.method ?? 'GET', signal: init?.signal as AbortSignal | undefined });
    const { status, body } = answer(String(url));
    return {
      status,
      ok: status >= 200 && status < 300,
      headers: { get: () => null },
      text: async () => JSON.stringify(body),
    } as unknown as Response;
  }) as unknown as typeof fetch;
});

afterEach(() => client.clear());

const load = () => client.fetchInfiniteQuery(bookingsListOptions());

describe('bookingsListOptions', () => {
  it('requests page 1 with the effective page size and returns bookings newest first even when fixtures are out of order', async () => {
    answer = () => ({
      status: 200,
      body: page([
        booking(1, '2026-10-20T09:00:00Z'),
        booking(3, '2026-10-21T12:00:00Z'),
        booking(2, '2026-10-21T10:00:00Z'),
      ]),
    });

    await load();
    const list = client.getQueryData(bookingsListKey(20));

    expect(requested[0].url).toBe('http://api.test:5001/api/v1/bookings?page=1&pageSize=20');
    expect(requested[0].method).toBe('GET');
    expect(requested[0].signal).toBeInstanceOf(AbortSignal);
    const observer = new InfiniteQueryObserver(client, bookingsListOptions());
    expect(observer.getCurrentResult().data?.bookings.map((b) => b.createdAt)).toEqual([
      '2026-10-21T12:00:00Z',
      '2026-10-21T10:00:00Z',
      '2026-10-20T09:00:00Z',
    ]);
    expect(list).toBeDefined();
  });

  it('follows the real pagination: the next page is page 2 and the reach is reported', async () => {
    answer = (url) =>
      url.includes('page=1')
        ? { status: 200, body: page([booking(2, '2026-10-21T10:00:00Z')], 1, true) }
        : { status: 200, body: page([booking(1, '2026-10-20T09:00:00Z')], 2, false) };
    const observer = new InfiniteQueryObserver(client, bookingsListOptions());
    const unsubscribe = observer.subscribe(() => undefined);

    await observer.refetch();
    expect(observer.getCurrentResult().data).toMatchObject({ hasMore: true });
    expect(observer.getCurrentResult().data?.bookings).toHaveLength(1);

    await observer.fetchNextPage();
    unsubscribe();

    expect(requested.map((r) => r.url.split('?')[1])).toEqual(['page=1&pageSize=20', 'page=2&pageSize=20']);
    expect(observer.getCurrentResult().data?.bookings).toHaveLength(2);
    expect(observer.getCurrentResult().data?.hasMore).toBe(false);
  });

  it('keeps the key tied to the effective parameters', () => {
    expect(bookingsListKey(20)).toEqual(['bookings', 'list', { pageSize: 20 }]);
    expect(bookingsListKey(50)).not.toEqual(bookingsListKey(20));
  });

  it.each([
    ['a booking without items', page([{ ...booking(1, '2026-10-21T10:00:00Z'), items: [] }])],
    ['a missing hasNextPage', { items: [], page: 1, pageSize: 20 }],
    ['a non-UUID booking id', page([{ ...booking(1, '2026-10-21T10:00:00Z'), bookingId: 'abc' }])],
    ['a balance as string', page([{ ...booking(1, '2026-10-21T10:00:00Z'), remainingBalanceCents: '3110' }])],
  ])('rejects %s instead of showing a partial list', async (_name, body) => {
    answer = () => ({ status: 200, body });

    await expect(load()).rejects.toThrow();
  });

  it('accepts a status it does not know so the booking can be shown as not recognized', async () => {
    answer = () => ({ status: 200, body: page([booking(1, '2026-10-21T10:00:00Z', 'teleported')]) });

    await load();
    const observer = new InfiniteQueryObserver(client, bookingsListOptions());

    expect(observer.getCurrentResult().data?.bookings[0].items[0].status).toBe('teleported');
  });

  it('surfaces a missing endpoint as an error, never as an empty list', async () => {
    answer = () => ({ status: 404, body: { errorCode: 'NOT_FOUND' } });

    await expect(load()).rejects.toMatchObject({ status: 404 });
    expect(client.getQueryData(bookingsListKey(20))).toBeUndefined();
  });
});
