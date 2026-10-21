import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react-native';
import { randomUUID } from 'expo-crypto';
import type { ReactNode } from 'react';

import { createTestQueryClient } from '../../../test/queryClient';
import { useBookingSubmission } from '../useBookingSubmission';

jest.mock('expo-crypto', () => ({ randomUUID: jest.fn() }));

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

type Sent = { key: string | null; body: string };
let sent: Sent[];
let resolvers: ((response: Response) => void)[];

function response(status: number, body: unknown): Response {
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => (typeof body === 'string' ? body : JSON.stringify(body)),
  } as unknown as Response;
}

/** Each call records the request and answers according to `answer`, or waits until `release` is called. */
function installFetch(answer?: () => Promise<Response>) {
  globalThis.fetch = jest.fn((_url: string | URL | Request, init?: RequestInit) => {
    sent.push({ key: new Headers(init?.headers).get('idempotency-key'), body: String(init?.body) });
    return answer ? answer() : new Promise<Response>((resolve) => resolvers.push(resolve));
  }) as unknown as typeof fetch;
}

const keys = (...values: string[]) => {
  let i = 0;
  return () => values[i++] ?? `key-${i}`;
};

let client: QueryClient;
const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={client}>{children}</QueryClientProvider>
);

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  sent = [];
  resolvers = [];
  client = createTestQueryClient();
});

afterEach(() => {
  resolvers.forEach((resolve) => resolve(response(503, {})));
});

describe('useBookingSubmission', () => {
  it('sends at most one initial request when the button is tapped twice before the next render', async () => {
    installFetch();
    const generateKey = keys('k1', 'k2');
    const { result } = await renderHook(() => useBookingSubmission({ generateKey }), { wrapper });

    await act(async () => {
      void result.current.submit(REQUEST);
      void result.current.submit(REQUEST);
    });

    expect(sent).toHaveLength(1);
    expect(result.current.state.status).toBe('submitting');
    expect(sent[0].key).toBe('k1');
  });

  it('takes a fresh key from expo-crypto for every intent by default', async () => {
    const random = jest.mocked(randomUUID);
    random.mockReturnValueOnce('11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa').mockReturnValueOnce('22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb');
    installFetch(async () => response(409, { errorCode: 'PRICE_CHANGED' }));
    const { result } = await renderHook(() => useBookingSubmission(), { wrapper });

    await act(async () => result.current.submit(REQUEST));
    await act(async () => result.current.submit(REQUEST));

    expect(sent.map((s) => s.key)).toEqual([
      '11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      '22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
    ]);
  });

  it('freezes the payload when the intent is created', async () => {
    installFetch();
    const generateKey = keys('k1');
    const { result } = await renderHook(() => useBookingSubmission({ generateKey }), { wrapper });
    const request = { serviceCodes: ['wash'], expectedTotalCents: 1890 };

    await act(async () => {
      void result.current.submit(request);
    });
    request.serviceCodes.push('dry');
    request.expectedTotalCents = 1;

    const state = result.current.state;
    if (state.status !== 'submitting') throw new Error('expected submitting');
    expect(JSON.parse(sent[0].body)).toEqual({ serviceCodes: ['wash'], expectedTotalCents: 1890 });
    expect(state.intent.request).toEqual({ serviceCodes: ['wash'], expectedTotalCents: 1890 });
    expect(state.intent.body).toBe(sent[0].body);
  });

  it.each([
    ['201 created', 201],
    ['200 replay', 200],
  ])('treats %s as a single confirmed booking', async (_name, status) => {
    installFetch(async () => response(status, BOOKING));
    const generateKey = keys('k1');
    const { result } = await renderHook(() => useBookingSubmission({ generateKey }), { wrapper });

    await act(async () => result.current.submit(REQUEST));

    expect(result.current.state).toMatchObject({ status: 'confirmed', booking: BOOKING });
    expect(sent[0].key).toBe('k1');
  });

  it('marks an allowlisted refusal as rejected and uses a new key for the next intent', async () => {
    installFetch(async () => response(409, { errorCode: 'WALLET_INSUFFICIENT_BALANCE' }));
    const generateKey = keys('k1', 'k2');
    const { result } = await renderHook(() => useBookingSubmission({ generateKey }), { wrapper });

    await act(async () => result.current.submit(REQUEST));
    expect(result.current.state).toMatchObject({ status: 'rejected', reason: 'insufficient-balance' });

    await act(async () => result.current.submit(REQUEST));
    expect(sent.map((s) => s.key)).toEqual(['k1', 'k2']);
  });

  it.each([
    ['server error', () => Promise.resolve(response(503, { errorCode: 'DATABASE_UNAVAILABLE' })), 'server-error'],
    ['network failure', () => Promise.reject(new TypeError('Network request failed')), 'network'],
    ['2xx with invalid JSON', () => Promise.resolve(response(201, '<html>')), 'invalid-response'],
    ['2xx with a body that breaks the contract', () => Promise.resolve(response(201, { bookingId: 'x' })), 'invalid-response'],
    ['unlisted 4xx', () => Promise.resolve(response(418, { errorCode: 'TEAPOT' })), 'unclassified-error'],
    ['4xx without ProblemDetails', () => Promise.resolve(response(400, '<html>')), 'unclassified-error'],
  ])('keeps key and payload in unknown after %s, without declaring failure or success', async (_name, answer, cause) => {
    installFetch(answer);
    const generateKey = keys('k1', 'k2');
    const { result } = await renderHook(() => useBookingSubmission({ generateKey }), { wrapper });

    await act(async () => result.current.submit(REQUEST));

    const state = result.current.state;
    expect(state).toMatchObject({ status: 'unknown', cause });
    if (state.status !== 'unknown') throw new Error('expected unknown');
    expect(state.intent.key).toBe('k1');
    expect(state.intent.body).toBe(sent[0].body);

    await act(async () => result.current.submit(REQUEST));
    expect(sent).toHaveLength(1);
  });

  it('keeps an idempotency conflict blocked: no new key and no automatic resolution', async () => {
    installFetch(async () => response(409, { errorCode: 'IDEMPOTENCY_CONFLICT' }));
    const generateKey = keys('k1', 'k2');
    const { result } = await renderHook(() => useBookingSubmission({ generateKey }), { wrapper });

    await act(async () => result.current.submit(REQUEST));
    await act(async () => result.current.submit(REQUEST));
    await act(async () => result.current.dismiss());

    expect(result.current.state).toMatchObject({ status: 'unknown', cause: 'idempotency-conflict' });
    expect(sent).toHaveLength(1);
  });

  it('treats a configuration error raised before any request as a certain rejection', async () => {
    delete process.env.EXPO_PUBLIC_API_URL;
    installFetch(async () => response(201, BOOKING));
    const { result } = await renderHook(() => useBookingSubmission({ generateKey: () => 'k1' }), { wrapper });

    await act(async () => result.current.submit(REQUEST));

    expect(result.current.state).toMatchObject({ status: 'rejected', reason: 'configuration' });
    expect(sent).toHaveLength(0);
  });
});
