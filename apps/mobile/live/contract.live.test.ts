/**
 * Contract check against the REAL API (gates C1, C2, C3). Not part of `pnpm test`.
 *
 *   EXPO_PUBLIC_API_URL=http://127.0.0.1:5001 pnpm --filter @cicclo/mobile test:live
 *
 * Needs a freshly bootstrapped demo base (wallet 5000, wash 1890, dry 2090, no bookings) and DEBITS the demo
 * wallet once (one wash, 5000 -> 3110). Reset the demo (see the backend README) before running it again.
 * It reuses the app's own transport, schemas and failure classification, so it proves what the app really does.
 */
import { QueryClient } from '@tanstack/react-query';
import { randomUUID } from 'node:crypto';
import http from 'node:http';

import { ApiError } from '../src/shared/api/apiError';
import { requestJson } from '../src/shared/api/client';
import { bookingDetailOptions } from '../src/features/bookings/detailQuery';
import { bookingsListOptions } from '../src/features/bookings/listQuery';
import { catalogOptions } from '../src/features/catalog/queries';
import { classifyFailure } from '../src/features/checkout/useBookingSubmission';
import { createBooking, serializeBookingBody } from '../src/features/checkout/createBooking';
import { walletOptions } from '../src/features/wallet/queries';

jest.setTimeout(240_000);

// The React Native Jest environment replaces fetch with a stub that never talks HTTP; use a minimal real one.
globalThis.fetch = ((input: string | URL | Request, init?: RequestInit) =>
  new Promise<Response>((resolve, reject) => {
    const request = http.request(
      String(input),
      { method: init?.method ?? 'GET', headers: init?.headers as Record<string, string> },
      (response) => {
        const chunks: Buffer[] = [];
        response.on('data', (chunk: Buffer) => chunks.push(chunk));
        response.on('end', () => {
          const status = response.statusCode ?? 0;
          resolve({
            status,
            ok: status >= 200 && status < 300,
            headers: { get: (name: string) => (response.headers[name.toLowerCase()] as string | undefined) ?? null },
            text: async () => Buffer.concat(chunks).toString('utf8'),
          } as unknown as Response);
        });
      },
    );
    request.on('error', reject);
    init?.signal?.addEventListener('abort', () => request.destroy(new Error('aborted')));
    if (init?.body !== undefined) request.write(init.body as string);
    request.end();
  })) as typeof fetch;

const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } } });
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const wallet = async () => (await client.fetchQuery({ ...walletOptions(), staleTime: 0 })).balanceCents;

async function rawPost(body: string, key: string | null = randomUUID()): Promise<unknown> {
  return requestJson({ method: 'POST', path: '/bookings', body, headers: key ? { 'Idempotency-Key': key } : {} });
}
async function failure(run: () => Promise<unknown>): Promise<ApiError> {
  try {
    await run();
  } catch (error) {
    if (error instanceof ApiError) return error;
    throw error;
  }
  throw new Error('expected the API to refuse');
}
const body = (serviceCodes: string[], expectedTotalCents: number, preferredFirstServiceCode?: string) =>
  serializeBookingBody({ serviceCodes, expectedTotalCents, preferredFirstServiceCode });

describe('C1: reads', () => {
  it('starts from the documented demo base', async () => {
    expect(await wallet()).toBe(5000);
    const catalog = await client.fetchQuery({ ...catalogOptions(), staleTime: 0 });
    expect(catalog.services.map((s) => [s.code, s.unitPriceCents, s.canBeBooked])).toEqual([
      ['dry', 2090, true],
      ['wash', 1890, true],
    ]);
    const list = await client.fetchInfiniteQuery(bookingsListOptions());
    expect(list.pages[0]).toMatchObject({ items: [], page: 1, hasNextPage: false });
  });
});

describe('C2: purchase contract', () => {
  it('window 1: definitive refusals leave the wallet untouched, and the sixth call is rate limited', async () => {
    const results: Record<string, { status: number; errorCode: string | null; outcome: unknown }> = {};
    const record = async (name: string, run: () => Promise<unknown>) => {
      const error = await failure(run);
      results[name] = { status: error.status, errorCode: error.errorCode, outcome: classifyFailure(error) };
    };

    await record('missing key', () => rawPost(body(['wash'], 1890), null));
    await record('eleven codes', () => rawPost(body(Array(11).fill('wash'), 20790)));
    await record('unknown service', () => rawPost(body(['iron'], 1000)));
    await record('wrong total', () => rawPost(body(['wash'], 1000)));
    await record('duplicates priced twice', () => rawPost(body(['wash', 'wash'], 1890)));

    expect(results['missing key']).toMatchObject({ status: 400, errorCode: 'VALIDATION_ERROR' });
    expect(results['eleven codes']).toMatchObject({ status: 400, errorCode: 'INVALID_SERVICE_SELECTION' });
    expect(results['unknown service']).toMatchObject({ status: 404, errorCode: 'SERVICE_NOT_FOUND' });
    expect(results['wrong total']).toMatchObject({ status: 409, errorCode: 'PRICE_CHANGED' });
    expect(results['duplicates priced twice']).toMatchObject({ status: 409, errorCode: 'PRICE_CHANGED' });
    for (const name of ['missing key', 'eleven codes', 'unknown service', 'wrong total', 'duplicates priced twice']) {
      expect(results[name].outcome).toMatchObject({ status: 'rejected' });
    }

    const limited = await failure(() => rawPost(body(['wash'], 1890)));
    console.log('429 observed:', JSON.stringify({ status: limited.status, errorCode: limited.errorCode, retryAfterAt: limited.retryAfterAt }));
    expect(limited.status).toBe(429);
    expect(limited.retryAfterAt).not.toBeNull();
    expect(classifyFailure(limited)).toMatchObject({ status: 'rejected', reason: 'rate-limited', retryAfterAt: limited.retryAfterAt });

    expect(await wallet()).toBe(5000);
    await sleep(Math.max(0, (limited.retryAfterAt ?? Date.now()) - Date.now()) + 1500);
  });

  it('window 2: insufficient balance, preference outside the batch, purchase 5000 -> 3110, replay and conflict', async () => {
    const insufficient = await failure(() => rawPost(body(['dry', 'dry', 'dry'], 6270)));
    expect(insufficient).toMatchObject({ status: 409, errorCode: 'WALLET_INSUFFICIENT_BALANCE' });
    expect(classifyFailure(insufficient)).toMatchObject({ status: 'rejected', reason: 'insufficient-balance' });

    const badPreference = await failure(() => rawPost(body(['wash'], 1890, 'dry')));
    expect(badPreference).toMatchObject({ status: 400, errorCode: 'INVALID_SERVICE_SELECTION' });
    expect(await wallet()).toBe(5000);

    const key = randomUUID();
    const sent = body(['wash'], 1890);
    const created = await createBooking({ idempotencyKey: key, body: sent });
    expect(created).toMatchObject({ chargedAmountCents: 1890, remainingBalanceCents: 3110, currency: 'BRL' });
    expect(created.items).toHaveLength(1);
    expect(created.items[0]).toMatchObject({ serviceCode: 'wash', unitPriceCents: 1890 });
    console.log('purchase items:', JSON.stringify(created.items));

    // Consistency: the very next read already shows the debited balance.
    expect(await wallet()).toBe(3110);

    const replay = await createBooking({ idempotencyKey: key, body: sent });
    expect(replay.bookingId).toBe(created.bookingId);
    expect(replay.chargedAmountCents).toBe(1890);
    expect(await wallet()).toBe(3110);

    const conflict = await failure(() => rawPost(body(['dry'], 2090), key));
    expect(conflict).toMatchObject({ status: 409, errorCode: 'IDEMPOTENCY_CONFLICT' });
    expect(classifyFailure(conflict)).toMatchObject({ status: 'unknown', cause: 'idempotency-conflict' });
    expect(await wallet()).toBe(3110);

    process.env.LIVE_BOOKING_ID = created.bookingId;
  });
});

describe('C3: list and detail after the purchase', () => {
  it('lists the new booking once, newest first, and serves its detail with historical prices', async () => {
    const id = process.env.LIVE_BOOKING_ID as string;
    expect(id).toBeDefined();

    client.removeQueries();
    const list = await client.fetchInfiniteQuery(bookingsListOptions());
    expect(list.pages[0].items.map((b) => b.bookingId)).toEqual([id]);
    expect(list.pages[0]).toMatchObject({ page: 1, pageSize: 20, hasNextPage: false });

    const detail = await client.fetchQuery({ ...bookingDetailOptions(id), staleTime: 0 });
    expect(detail.bookingId).toBe(id);
    expect(detail.chargedAmountCents).toBe(1890);
    expect(detail.items[0]).toMatchObject({ serviceCode: 'wash', unitPriceCents: 1890 });
    console.log('detail item:', JSON.stringify(detail.items[0]));
    console.log('item statuses seen:', detail.items.map((i) => i.status).join(','));
  });

  it('answers 404 BOOKING_NOT_FOUND for an unknown id and 400 for a malformed one', async () => {
    const missing = await failure(() => requestJson({ method: 'GET', path: `/bookings/${randomUUID()}` }));
    expect(missing).toMatchObject({ status: 404, errorCode: 'BOOKING_NOT_FOUND' });
    const malformed = await failure(() => requestJson({ method: 'GET', path: '/bookings/abc' }));
    expect(malformed).toMatchObject({ status: 400, errorCode: 'VALIDATION_ERROR' });
  });

  it('paginates with the effective parameters', async () => {
    const page = await requestJson({ method: 'GET', path: '/bookings?page=2&pageSize=1' });
    expect(page).toMatchObject({ items: [], page: 2, pageSize: 1, hasNextPage: false });
    const invalid = await failure(() => requestJson({ method: 'GET', path: '/bookings?page=0&pageSize=500' }));
    expect(invalid).toMatchObject({ status: 400, errorCode: 'VALIDATION_ERROR' });
  });
});
