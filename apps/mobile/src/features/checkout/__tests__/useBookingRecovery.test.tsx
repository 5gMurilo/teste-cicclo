import { onlineManager, QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react-native';
import type { ReactNode } from 'react';

import { createTestQueryClient } from '../../../test/queryClient';
import { catalogKey } from '../../catalog/queries';
import { walletKey } from '../../wallet/queries';
import { useBookingSubmission } from '../useBookingSubmission';

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
const REQUEST = { serviceCodes: ['wash'], expectedTotalCents: 1890 };
const T0 = Date.parse('2026-10-21T12:00:00Z');

type Sent = { method: string; url: string; key: string | null; body: string | undefined };
let sent: Sent[];
let answers: (() => Promise<Response>)[];
let clock: number;
let client: QueryClient;

function response(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  const lower = Object.fromEntries(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), v]));
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: { get: (name: string) => lower[name.toLowerCase()] ?? null },
    text: async () => JSON.stringify(body),
  } as unknown as Response;
}

/** Answers POSTs from the queue, in order; any other request gets an empty 200. */
function installFetch(...queue: (() => Promise<Response>)[]) {
  answers = queue;
  globalThis.fetch = jest.fn((url: string | URL | Request, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    sent.push({
      method,
      url: String(url),
      key: new Headers(init?.headers).get('idempotency-key'),
      body: init?.body as string | undefined,
    });
    return method === 'POST' ? (answers.shift() ?? (() => Promise.reject(new TypeError('no answer'))))() : Promise.resolve(response(200, {}));
  }) as unknown as typeof fetch;
}

const reply = (status: number, body: unknown, headers?: Record<string, string>) => () =>
  Promise.resolve(response(status, body, headers));
const fail = () => Promise.reject(new TypeError('Network request failed'));
const posts = () => sent.filter((s) => s.method === 'POST');

const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={client}>{children}</QueryClientProvider>
);

function mount(options: { keyRetentionMs?: number | null } = {}) {
  let n = 0;
  const generateKey = () => `key-${++n}`;
  const now = () => new Date(clock);
  return renderHook(() => useBookingSubmission({ generateKey, now, ...options }), { wrapper });
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  sent = [];
  clock = T0;
  client = createTestQueryClient();
  // The transport stamps Retry-After deadlines with Date.now(), so the test clock must be the same one.
  jest.spyOn(Date, 'now').mockImplementation(() => clock);
});

afterEach(() => jest.restoreAllMocks());

describe('explicit verification of an unknown purchase', () => {
  it('replays the same key and the same bytes, and confirms the replay', async () => {
    installFetch(fail, reply(200, { ...BOOKING, remainingBalanceCents: 1020 }));
    const { result } = await mount();

    await act(async () => result.current.submit(REQUEST));
    expect(result.current.state.status).toBe('unknown');
    await act(async () => result.current.verify());

    expect(posts()).toHaveLength(2);
    expect(posts()[1].key).toBe(posts()[0].key);
    expect(posts()[1].body).toBe(posts()[0].body);
    expect(result.current.state).toMatchObject({ status: 'confirmed', booking: { remainingBalanceCents: 1020 } });
  });

  it('does nothing unless the state is a verifiable unknown', async () => {
    installFetch(reply(201, BOOKING));
    const { result } = await mount();

    await act(async () => result.current.verify());
    await act(async () => result.current.submit(REQUEST));
    await act(async () => result.current.verify());

    expect(posts()).toHaveLength(1);
  });

  it('never posts again by itself when the connection comes back', async () => {
    installFetch(fail, reply(200, BOOKING));
    const { result } = await mount();

    await act(async () => result.current.submit(REQUEST));
    await act(async () => {
      onlineManager.setOnline(false);
      onlineManager.setOnline(true);
    });

    expect(posts()).toHaveLength(1);
    expect(result.current.state.status).toBe('unknown');
  });

  it('blocks verification after an idempotency conflict and after the retention window', async () => {
    installFetch(reply(409, { errorCode: 'IDEMPOTENCY_CONFLICT' }));
    const conflicted = await mount();
    await act(async () => conflicted.result.current.submit(REQUEST));
    await act(async () => conflicted.result.current.verify());
    expect(posts()).toHaveLength(1);
    expect(conflicted.result.current.state).toMatchObject({ status: 'unknown', cause: 'idempotency-conflict' });

    sent = [];
    installFetch(fail);
    const { result } = await mount({ keyRetentionMs: 60_000 });
    await act(async () => result.current.submit(REQUEST));
    clock = T0 + 60_000;
    await act(async () => result.current.verify());

    expect(posts()).toHaveLength(1);
    expect(result.current.state).toMatchObject({ status: 'unknown', cause: 'retention-expired' });
    await act(async () => result.current.submit(REQUEST));
    expect(posts()).toHaveLength(1);
  });
});

describe('definitive refusals', () => {
  it('PRICE_CHANGED is rejected without silent resubmission, refreshes wallet and catalog, and needs a new intent with a new key', async () => {
    client.setQueryData(walletKey, { balanceCents: 5000 });
    client.setQueryData(catalogKey, { services: [] });
    installFetch(reply(409, { errorCode: 'PRICE_CHANGED' }), reply(201, BOOKING));
    const { result } = await mount();

    await act(async () => result.current.submit(REQUEST));

    expect(result.current.state).toMatchObject({ status: 'rejected', reason: 'price-changed' });
    expect(posts()).toHaveLength(1);
    expect(client.getQueryState(walletKey)?.isInvalidated).toBe(true);
    expect(client.getQueryState(catalogKey)?.isInvalidated).toBe(true);

    await act(async () => result.current.submit({ serviceCodes: ['wash'], expectedTotalCents: 2500 }));
    expect(posts().map((p) => p.key)).toEqual(['key-1', 'key-2']);
    expect(JSON.parse(posts()[1].body ?? '{}').expectedTotalCents).toBe(2500);
  });

  it('a real insufficient-balance refusal refreshes the wallet and never reads as a charge', async () => {
    client.setQueryData(walletKey, { balanceCents: 5000 });
    installFetch(reply(409, { errorCode: 'WALLET_INSUFFICIENT_BALANCE' }));
    const { result } = await mount();

    await act(async () => result.current.submit(REQUEST));

    expect(result.current.state).toMatchObject({ status: 'rejected', reason: 'insufficient-balance' });
    expect(client.getQueryState(walletKey)?.isInvalidated).toBe(true);
  });
});

describe('rate limiting', () => {
  it('blocks a new attempt until the Retry-After deadline given in seconds, with no automatic POST', async () => {
    installFetch(reply(429, { errorCode: 'RATE_LIMITED' }, { 'Retry-After': '30' }), reply(201, BOOKING));
    const { result } = await mount();

    await act(async () => result.current.submit(REQUEST));
    expect(result.current.state).toMatchObject({
      status: 'rejected',
      reason: 'rate-limited',
      retryAfterAt: T0 + 30_000,
    });

    clock = T0 + 29_999;
    await act(async () => result.current.submit(REQUEST));
    expect(posts()).toHaveLength(1);

    clock = T0 + 30_000;
    await act(async () => result.current.submit(REQUEST));
    expect(posts()).toHaveLength(2);
    expect(result.current.state.status).toBe('confirmed');
  });

  it('understands an HTTP-date deadline and allows a manual retry when the header is absent or invalid', async () => {
    installFetch(reply(429, {}, { 'Retry-After': 'Wed, 21 Oct 2026 12:01:00 GMT' }));
    const dated = await mount();
    await act(async () => dated.result.current.submit(REQUEST));
    expect(dated.result.current.state).toMatchObject({ retryAfterAt: Date.parse('2026-10-21T12:01:00Z') });

    for (const headers of [{}, { 'Retry-After': 'soon' }] as Record<string, string>[]) {
      sent = [];
      installFetch(reply(429, {}, headers), reply(201, BOOKING));
      const { result } = await mount();
      await act(async () => result.current.submit(REQUEST));
      expect(result.current.state).toMatchObject({ status: 'rejected', reason: 'rate-limited', retryAfterAt: null });
      await act(async () => result.current.submit(REQUEST));
      expect(posts()).toHaveLength(2);
    }
  });

  it('keeps an earlier unknown purchase unknown when its verification is rate limited', async () => {
    installFetch(fail, reply(429, {}, { 'Retry-After': '10' }), reply(200, BOOKING));
    const { result } = await mount();

    await act(async () => result.current.submit(REQUEST));
    await act(async () => result.current.verify());
    expect(result.current.state).toMatchObject({ status: 'unknown', cause: 'rate-limited', retryAfterAt: T0 + 10_000 });

    await act(async () => result.current.verify());
    expect(posts()).toHaveLength(2);

    clock = T0 + 10_000;
    await act(async () => result.current.verify());
    expect(posts()).toHaveLength(3);
    expect(result.current.state.status).toBe('confirmed');
    expect(new Set(posts().map((p) => p.key)).size).toBe(1);
  });
});
