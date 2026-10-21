import { QueryClientProvider, QueryObserver, type QueryClient } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react-native';
import type { ReactNode } from 'react';

import { bookingsKey } from '../../bookings/queryKeys';
import { walletKey, walletOptions } from '../../wallet/queries';
import { createTestQueryClient } from '../../../test/queryClient';
import { applyConfirmedBooking } from '../applyConfirmedBooking';
import type { ConfirmedBooking } from '../createBooking';
import { useBookingSubmission } from '../useBookingSubmission';

const BOOKING: ConfirmedBooking = {
  bookingId: '11111111-1111-4111-8111-111111111111',
  currency: 'BRL',
  chargedAmountCents: 1890,
  remainingBalanceCents: 3110,
  createdAt: '2026-10-21T12:00:00Z',
  items: [],
};
const WALLET_5000 = { balanceCents: 5000, currency: 'BRL' as const, updatedAt: '2026-10-21T11:00:00Z' };

let client: QueryClient;

function respond(status: number, body: unknown): Response {
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response;
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  client = createTestQueryClient();
});

afterEach(() => client.clear());

describe('applyConfirmedBooking', () => {
  it('keeps the confirmed balance when a GET that started before the purchase answers late', async () => {
    client.setQueryData(walletKey, WALLET_5000);
    let answerLate: (response: Response) => void = () => undefined;
    // A transport that ignores the abort and resolves only after the purchase was applied.
    globalThis.fetch = jest.fn(() => new Promise<Response>((resolve) => (answerLate = resolve))) as unknown as typeof fetch;
    const oldGet = client.fetchQuery({ ...walletOptions(), staleTime: 0 }).catch(() => undefined);
    await Promise.resolve();

    await applyConfirmedBooking(client, BOOKING);
    answerLate(respond(200, WALLET_5000));
    await oldGet;

    expect(client.getQueryData(walletKey)).toMatchObject({ balanceCents: 3110 });
  });

  it('writes the confirmed balance without inventing updatedAt, and marks the wallet for refetch', async () => {
    client.setQueryData(walletKey, WALLET_5000);

    await applyConfirmedBooking(client, BOOKING);

    expect(client.getQueryData(walletKey)).toEqual({ ...WALLET_5000, balanceCents: 3110 });
    expect(client.getQueryState(walletKey)?.isInvalidated).toBe(true);
  });

  it('does not fabricate a wallet when none is cached', async () => {
    await applyConfirmedBooking(client, BOOKING);

    expect(client.getQueryData(walletKey)).toBeUndefined();
  });

  it('invalidates wallet and every bookings query but leaves the catalog alone', async () => {
    client.setQueryData(walletKey, WALLET_5000);
    client.setQueryData([...bookingsKey, 'list', {}], { bookings: [] });
    client.setQueryData([...bookingsKey, 'detail', 'abc'], { id: 'abc' });
    client.setQueryData(['services'], { services: [] });

    await applyConfirmedBooking(client, BOOKING);

    expect(client.getQueryState([...bookingsKey, 'list', {}])?.isInvalidated).toBe(true);
    expect(client.getQueryState([...bookingsKey, 'detail', 'abc'])?.isInvalidated).toBe(true);
    expect(client.getQueryState(['services'])?.isInvalidated).toBe(false);
  });
});

describe('confirmation survives cache problems', () => {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const REQUEST = { serviceCodes: ['wash'], expectedTotalCents: 1890 };
  const bookingJson = { ...BOOKING, items: [] };

  function installFetch(onGet: () => Promise<Response>) {
    const posts: string[] = [];
    globalThis.fetch = jest.fn((_url: string | URL | Request, init?: RequestInit) => {
      if (init?.method === 'POST') {
        posts.push('post');
        return Promise.resolve(respond(201, bookingJson));
      }
      return onGet();
    }) as unknown as typeof fetch;
    return posts;
  }

  it('stays confirmed, with a single POST, when the refetch that follows fails', async () => {
    client.setQueryData(walletKey, WALLET_5000);
    const observer = new QueryObserver(client, { ...walletOptions(), retry: false });
    const unsubscribe = observer.subscribe(() => undefined);
    const posts = installFetch(() => Promise.resolve(respond(404, { errorCode: 'NOT_FOUND' })));
    const { result } = await renderHook(() => useBookingSubmission({ generateKey: () => 'k1' }), { wrapper });

    await act(async () => result.current.submit(REQUEST));
    unsubscribe();

    expect(result.current.state).toMatchObject({ status: 'confirmed', booking: { remainingBalanceCents: 3110 } });
    expect(posts).toHaveLength(1);
  });

  it('stays confirmed, with a single POST, when applying the cache throws', async () => {
    const posts = installFetch(() => Promise.resolve(respond(200, WALLET_5000)));
    jest.spyOn(client, 'cancelQueries').mockRejectedValue(new Error('cache exploded'));
    const { result } = await renderHook(() => useBookingSubmission({ generateKey: () => 'k1' }), { wrapper });

    await act(async () => result.current.submit(REQUEST));

    expect(result.current.state.status).toBe('confirmed');
    expect(posts).toHaveLength(1);
  });
});
