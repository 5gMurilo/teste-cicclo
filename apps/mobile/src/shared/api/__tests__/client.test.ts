import { ApiError } from '../apiError';
import { requestJson, TransportError } from '../client';

type FakeInit = RequestInit & { signal: AbortSignal };
type Handler = (url: string, init: FakeInit) => Promise<Response>;

function reply(status: number, body: string, headers: Record<string, string> = {}): Promise<Response> {
  const lower = Object.fromEntries(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), v]));
  return Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: (name: string) => lower[name.toLowerCase()] ?? null },
    text: async () => body,
  } as unknown as Response);
}

// Faithful to fetch: never settles on its own, rejects with AbortError when the signal aborts.
function pendingUntilAborted(_url: string, init: FakeInit): Promise<Response> {
  return new Promise((_resolve, reject) => {
    init.signal.addEventListener('abort', () => {
      const error = new Error('The operation was aborted');
      error.name = 'AbortError';
      reject(error);
    });
  });
}

const calls: { url: string; init: FakeInit }[] = [];

function installFetch(handler: Handler) {
  globalThis.fetch = jest.fn(async (url: string | URL | Request, init?: RequestInit) => {
    calls.push({ url: String(url), init: init as FakeInit });
    return handler(String(url), init as FakeInit);
  }) as unknown as typeof fetch;
}

beforeEach(() => {
  calls.length = 0;
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
});

afterEach(() => {
  jest.useRealTimers();
});

describe('requestJson', () => {
  it('sends a GET under /api/v1 exactly once, with the signal and without a body', async () => {
    installFetch(() => reply(200, '{"balanceCents":5000}'));
    const controller = new AbortController();

    const result = await requestJson({ method: 'GET', path: '/wallet', signal: controller.signal });

    expect(result).toEqual({ balanceCents: 5000 });
    expect(calls[0].url).toBe('http://api.test:5001/api/v1/wallet');
    expect(calls[0].init.method).toBe('GET');
    expect(calls[0].init.body).toBeUndefined();
    expect(new Headers(calls[0].init.headers).get('accept')).toBe('application/json');
    expect(new Headers(calls[0].init.headers).get('content-type')).toBeNull();
    expect(calls[0].init.signal).toBeInstanceOf(AbortSignal);
  });

  it('sends the POST body byte for byte and keeps the caller headers', async () => {
    installFetch(() => reply(201, '{"id":"b1"}'));
    const body = '{"serviceCodes":["WASH","DRY"],  "expectedTotalCents":3980}';

    await requestJson({
      method: 'POST',
      path: '/bookings',
      body,
      headers: { 'Idempotency-Key': 'key-123' },
    });

    expect(calls[0].init.body).toBe(body);
    const headers = new Headers(calls[0].init.headers);
    expect(headers.get('idempotency-key')).toBe('key-123');
    expect(headers.get('content-type')).toBe('application/json');
  });

  it('resolves null for an empty 2xx body and rejects HTML or invalid JSON as invalid-body', async () => {
    installFetch(() => reply(202, ''));
    await expect(requestJson({ method: 'GET', path: '/x' })).resolves.toBeNull();

    for (const text of ['<html>ok</html>', '{"broken":']) {
      installFetch(() => reply(200, text));
      await expect(requestJson({ method: 'GET', path: '/x' })).rejects.toMatchObject({
        name: 'TransportError',
        kind: 'invalid-body',
        status: 200,
      });
    }
  });

  it('turns a non-2xx response into an ApiError with errorCode, traceId and Retry-After', async () => {
    installFetch(() =>
      reply(429, '{"errorCode":"RATE_LIMITED","traceId":"t-1","detail":"x"}', { 'Retry-After': '30' }),
    );
    const before = Date.now();

    const error = (await requestJson({ method: 'POST', path: '/bookings', body: '{}' }).catch(
      (e: unknown) => e,
    )) as ApiError;

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(429);
    expect(error.errorCode).toBe('RATE_LIMITED');
    expect(error.traceId).toBe('t-1');
    expect(error.retryAfterAt).toBeGreaterThanOrEqual(before + 30_000);
  });

  it('classifies a non-2xx HTML body by status alone', async () => {
    installFetch(() => reply(502, '<html>Bad Gateway</html>'));

    await expect(requestJson({ method: 'GET', path: '/x' })).rejects.toMatchObject({
      name: 'ApiError',
      status: 502,
      errorCode: null,
    });
  });

  it('reports a rejected fetch as a network TransportError', async () => {
    installFetch(() => Promise.reject(new TypeError('Network request failed')));

    await expect(requestJson({ method: 'GET', path: '/x' })).rejects.toMatchObject({
      name: 'TransportError',
      kind: 'network',
      status: null,
    });
  });

  it('propagates an external abort as an AbortError, not a network or timeout error', async () => {
    installFetch(pendingUntilAborted);
    const controller = new AbortController();

    const pending = requestJson({ method: 'GET', path: '/x', signal: controller.signal });
    controller.abort();

    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    expect(calls[0].init.signal.aborted).toBe(true);
    await expect(pending).rejects.not.toBeInstanceOf(TransportError);
  });

  it('times out after 15 seconds and aborts the underlying request', async () => {
    jest.useFakeTimers();
    installFetch(pendingUntilAborted);

    const pending = requestJson({ method: 'GET', path: '/x' }).catch((e: unknown) => e);
    await jest.advanceTimersByTimeAsync(14_999);
    expect(calls[0].init.signal.aborted).toBe(false);
    await jest.advanceTimersByTimeAsync(1);

    const error = (await pending) as TransportError;
    expect(error).toBeInstanceOf(TransportError);
    expect(error.kind).toBe('timeout');
    expect(calls[0].init.signal.aborted).toBe(true);
  });

  it('cleans up the timer and the external listener after completion', async () => {
    jest.useFakeTimers();
    installFetch(() => reply(200, '{}'));
    const controller = new AbortController();

    await requestJson({ method: 'GET', path: '/x', signal: controller.signal });

    expect(jest.getTimerCount()).toBe(0);
    controller.abort();
    expect(calls[0].init.signal.aborted).toBe(false);
  });
});
