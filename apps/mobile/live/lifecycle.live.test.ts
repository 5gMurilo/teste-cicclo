/**
 * P1 item commands against the REAL API (gate CP1). Not part of `pnpm test`.
 *
 *   EXPO_PUBLIC_API_URL=http://127.0.0.1:5001 pnpm --filter @cicclo/mobile test:live
 *
 * State it expects: the wallet holds 3110 and the only booking is one wash that is offered or queued (the
 * state `contract.live.test.ts` leaves behind). It then CHANGES the demo: cancels that wash (refund), buys a
 * wash+dry batch, activates both, confirms the wash (left in progress), reports it, and cancels the dry
 * (refund). The wallet ends at 3110. Reset the demo (backend README) before running again.
 * It reuses the app's own transport, schemas and parsers.
 */
import { randomUUID } from 'node:crypto';

import { parseCancelResponse } from '../src/features/bookings/cancelItem';
import { parseItemResponse, postItemCommand, type ItemAction } from '../src/features/bookings/itemCommand';
import { parseReportResponse } from '../src/features/bookings/reportIssue';
import { bookingSchema } from '../src/features/bookings/schema';
import { createBooking, serializeBookingBody } from '../src/features/checkout/createBooking';
import { ApiError } from '../src/shared/api/apiError';
import { requestJson } from '../src/shared/api/client';
import { installNodeFetch } from './nodeFetch';

jest.setTimeout(400_000);
installNodeFetch();

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const wallet = async () => ((await requestJson({ method: 'GET', path: '/wallet' })) as { balanceCents: number }).balanceCents;
const detail = async (bookingId: string) =>
  bookingSchema.parse(await requestJson({ method: 'GET', path: `/bookings/${bookingId}` }));

/** Sends a command; waits out a 429 once (the item command policies are per minute). */
async function command(bookingId: string, itemId: string, action: ItemAction): Promise<unknown> {
  try {
    return await postItemCommand({ bookingId, itemId, action });
  } catch (error) {
    if (error instanceof ApiError && error.status === 429 && error.retryAfterAt !== null) {
      console.log(`429 on ${action}, waiting for Retry-After`);
      await sleep(Math.max(0, error.retryAfterAt - Date.now()) + 1000);
      return postItemCommand({ bookingId, itemId, action });
    }
    throw error;
  }
}
async function refused(bookingId: string, itemId: string, action: ItemAction): Promise<ApiError> {
  try {
    await command(bookingId, itemId, action);
  } catch (error) {
    if (error instanceof ApiError) return error;
    throw error;
  }
  throw new Error(`expected ${action} to be refused`);
}

const state: { aBooking: string; aItem: string; batchBooking: string; washItem: string; dryItem: string } = {
  aBooking: '',
  aItem: '',
  batchBooking: '',
  washItem: '',
  dryItem: '',
};

describe('CP1: item commands on the real API', () => {
  it('phase 1, wash A: activate is idempotent, report is refused before it starts, confirm after the deadline is refused', async () => {
    const list = (await requestJson({ method: 'GET', path: '/bookings?page=1&pageSize=20' })) as { items: unknown[] };
    const booking = bookingSchema.parse(list.items[0]);
    expect(await wallet()).toBe(3110);
    expect(booking.items).toHaveLength(1);
    state.aBooking = booking.bookingId;
    state.aItem = booking.items[0].itemId;
    expect(['to_be_confirmed', 'queued']).toContain(booking.items[0].status);

    const activated = parseItemResponse(await command(state.aBooking, state.aItem, 'activate'));
    expect(activated.itemId).toBe(state.aItem);
    expect(['to_be_confirmed', 'queued']).toContain(activated.status);
    console.log('activate on an already active item:', JSON.stringify(activated));

    const early = await refused(state.aBooking, state.aItem, 'report-issue');
    expect(early).toMatchObject({ status: 409, errorCode: 'INVALID_STATE_TRANSITION' });

    const current = await detail(state.aBooking);
    const offer = current.items[0];
    if (offer.status === 'to_be_confirmed' && offer.expiresAt !== null) {
      await sleep(Math.max(0, Date.parse(offer.expiresAt) - Date.now()) + 400);
      const late = await refused(state.aBooking, state.aItem, 'confirm');
      console.log('confirm just after the deadline:', JSON.stringify({ status: late.status, errorCode: late.errorCode }));
      expect([409, 410]).toContain(late.status);
      expect(['MACHINE_OFFER_EXPIRED', 'INVALID_STATE_TRANSITION']).toContain(late.errorCode);
    } else {
      console.log('wash A was queued, so the expired-offer confirm was not exercised');
    }
  });

  it('phase 2, wash A: cancel refunds the historical price once, repeats do not credit again, a cancelled item refuses commands', async () => {
    const first = parseCancelResponse(await command(state.aBooking, state.aItem, 'cancel'));
    expect(first.item).toMatchObject({ itemId: state.aItem, status: 'cancelled', machineId: null, queuePosition: null, expiresAt: null });
    expect(first.refundedAmountCents).toBe(1890);
    expect(first.remainingBalanceCents).toBe(5000);
    expect(await wallet()).toBe(5000);

    const again = parseCancelResponse(await command(state.aBooking, state.aItem, 'cancel'));
    expect(again.refundedAmountCents).toBe(1890);
    expect(again.remainingBalanceCents).toBe(5000);
    expect(await wallet()).toBe(5000);

    for (const action of ['activate', 'confirm'] as const) {
      expect(await refused(state.aBooking, state.aItem, action)).toMatchObject({
        status: 409,
        errorCode: 'INVALID_STATE_TRANSITION',
      });
    }
    expect((await detail(state.aBooking)).items[0].status).toBe('cancelled');
  });

  it('phase 3: a wash+dry batch without preference starts paid and pending, activate moves each item', async () => {
    const created = await createBooking({
      idempotencyKey: randomUUID(),
      body: serializeBookingBody({ serviceCodes: ['wash', 'dry'], expectedTotalCents: 3980 }),
    });
    expect(created.remainingBalanceCents).toBe(1020);
    expect(created.items.map((i) => i.status)).toEqual(['pending_activation', 'pending_activation']);
    state.batchBooking = created.bookingId;
    state.washItem = created.items.find((i) => i.serviceCode === 'wash')!.itemId;
    state.dryItem = created.items.find((i) => i.serviceCode === 'dry')!.itemId;

    const wash = parseItemResponse(await command(state.batchBooking, state.washItem, 'activate'));
    const dry = parseItemResponse(await command(state.batchBooking, state.dryItem, 'activate'));
    console.log('activated wash:', JSON.stringify(wash));
    console.log('activated dry:', JSON.stringify(dry));
    expect(wash.status).toBe('to_be_confirmed');
    expect(wash.machineId).not.toBeNull();
    expect(wash.expiresAt).not.toBeNull();
    expect(dry.status).toBe('to_be_confirmed');
    expect(await wallet()).toBe(1020);
  });

  it('phase 4, the wash: confirm starts it, repeating does not restart it, cancel is refused, a report is accepted once', async () => {
    const confirmed = parseItemResponse(await command(state.batchBooking, state.washItem, 'confirm'));
    expect(confirmed).toMatchObject({ itemId: state.washItem, status: 'in_progress', expiresAt: null });
    expect(confirmed.machineId).not.toBeNull();

    const repeat = parseItemResponse(await command(state.batchBooking, state.washItem, 'confirm'));
    expect(repeat).toMatchObject({ status: 'in_progress', machineId: confirmed.machineId });

    expect(await refused(state.batchBooking, state.washItem, 'cancel')).toMatchObject({
      status: 409,
      errorCode: 'INVALID_STATE_TRANSITION',
    });

    const report = parseReportResponse(await command(state.batchBooking, state.washItem, 'report-issue'));
    expect(report.status).toBe('reported');
    const again = parseReportResponse(await command(state.batchBooking, state.washItem, 'report-issue'));
    expect(again.incidentId).toBe(report.incidentId);
    expect(await wallet()).toBe(1020);
    expect((await detail(state.batchBooking)).items.find((i) => i.itemId === state.washItem)?.status).toBe('in_progress');
  });

  it('phase 5, the dry: cancelling a not started item refunds its historical price', async () => {
    const cancelled = parseCancelResponse(await command(state.batchBooking, state.dryItem, 'cancel'));
    expect(cancelled.item.status).toBe('cancelled');
    expect(cancelled.refundedAmountCents).toBe(2090);
    expect(cancelled.remainingBalanceCents).toBe(3110);
    expect(await wallet()).toBe(3110);

    const final = await detail(state.batchBooking);
    expect(final.items.map((i) => [i.serviceCode, i.status])).toEqual([
      ['dry', 'cancelled'],
      ['wash', 'in_progress'],
    ]);
  });
});
