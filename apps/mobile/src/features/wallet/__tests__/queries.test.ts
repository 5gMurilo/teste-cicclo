import type { QueryClient } from '@tanstack/react-query';

import { ApiError } from '../../../shared/api/apiError';
import { TransportError } from '../../../shared/api/client';
import { shouldRetryRead } from '../../../shared/api/queryClient';
import { createTestQueryClient } from '../../../test/queryClient';
import { walletKey, walletOptions } from '../queries';

type FakeInit = RequestInit & { signal: AbortSignal };

const WALLET = { balanceCents: 5000, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' };

function reply(status: number, body: unknown): Promise<Response> {
  return Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);
}

let client: QueryClient;
let signals: AbortSignal[];

function installFetch(handler: (init: FakeInit) => Promise<Response>) {
  globalThis.fetch = jest.fn(async (_url: string | URL | Request, init?: RequestInit) => {
    signals.push((init as FakeInit).signal);
    return handler(init as FakeInit);
  }) as unknown as typeof fetch;
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  client = createTestQueryClient();
  signals = [];
});

afterEach(() => client.clear());

describe('walletOptions', () => {
  it('returns the confirmed balance in integer cents', async () => {
    installFetch(() => reply(200, WALLET));

    await expect(client.fetchQuery(walletOptions())).resolves.toEqual(WALLET);
    expect(client.getQueryData(walletKey)).toEqual(WALLET);
  });

  it.each([
    ['negative cents', { ...WALLET, balanceCents: -1 }],
    ['fractional cents', { ...WALLET, balanceCents: 5000.5 }],
    ['cents as a string', { ...WALLET, balanceCents: '5000' }],
    ['unsafe integer', { ...WALLET, balanceCents: 2 ** 53 }],
    ['above the contract range', { ...WALLET, balanceCents: 1_000_000_000_000 }],
    ['missing balance', { currency: 'BRL', updatedAt: WALLET.updatedAt }],
    ['wrong currency', { ...WALLET, currency: 'USD' }],
    ['invalid timestamp', { ...WALLET, updatedAt: 'ontem' }],
  ])('rejects %s without inventing a default balance', async (_name, body) => {
    installFetch(() => reply(200, body));

    await expect(client.fetchQuery(walletOptions())).rejects.toThrow();
    expect(client.getQueryData(walletKey)).toBeUndefined();
  });

  it('surfaces a failure and recovers on an explicit refetch', async () => {
    let calls = 0;
    installFetch(() => (++calls === 1 ? reply(503, { errorCode: 'UNAVAILABLE' }) : reply(200, WALLET)));

    await expect(client.fetchQuery(walletOptions())).rejects.toBeInstanceOf(ApiError);
    expect(client.getQueryData(walletKey)).toBeUndefined();
    await expect(client.fetchQuery(walletOptions())).resolves.toEqual(WALLET);
  });

  it('passes the query signal to the request so cancellation aborts it', async () => {
    installFetch(
      (init) =>
        new Promise((_resolve, reject) => {
          init.signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
        }),
    );

    const pending = client.fetchQuery(walletOptions()).catch((e: unknown) => e);
    await Promise.resolve();
    await client.cancelQueries({ queryKey: walletKey });
    await pending;

    expect(signals).toHaveLength(1);
    expect(signals[0].aborted).toBe(true);
    expect(client.getQueryData(walletKey)).toBeUndefined();
  });
});

describe('shouldRetryRead', () => {
  const api = (status: number) => new ApiError({ status, errorCode: null, traceId: null, retryAfterAt: null });

  it('retries one transient failure and nothing else', () => {
    expect(shouldRetryRead(0, new TransportError('network'))).toBe(true);
    expect(shouldRetryRead(0, new TransportError('timeout'))).toBe(true);
    expect(shouldRetryRead(0, api(503))).toBe(true);
    expect(shouldRetryRead(1, api(503))).toBe(false);
    expect(shouldRetryRead(0, api(404))).toBe(false);
    expect(shouldRetryRead(0, api(429))).toBe(false);
    expect(shouldRetryRead(0, new TransportError('invalid-body', 200))).toBe(false);
    expect(shouldRetryRead(0, new Error('ZodError-like'))).toBe(false);
  });
});
