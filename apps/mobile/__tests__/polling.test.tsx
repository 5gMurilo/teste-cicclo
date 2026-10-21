import { focusManager, onlineManager } from '@tanstack/react-query';
import { act } from '@testing-library/react-native';
import { fireEvent, renderRouter, screen } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

jest.mock('../src/shared/api/capabilities', () => ({
  capabilities: {
    purchase: true,
    keyRetentionMs: null,
    itemCommands: { activate: false, confirm: false, cancel: false, reportIssue: false },
  },
}));

const APP_DIR = './src/app';
const BOOKING_ID = '00000000-0000-4000-8000-000000000001';

const item = (status: string) => ({
  itemId: '10000000-0000-4000-8000-000000000001',
  serviceCode: 'wash',
  unitPriceCents: 1890,
  status,
  machineId: null,
  queuePosition: status === 'queued' ? 1 : null,
  expiresAt: null,
});
const booking = (status: string) => ({
  bookingId: BOOKING_ID,
  currency: 'BRL',
  chargedAmountCents: 1890,
  remainingBalanceCents: 3110,
  createdAt: '2026-10-21T12:00:00Z',
  items: [item(status)],
});

type Reply = { status: number; body: unknown };
let bookingStatus: string;
let listReply: (call: number) => Reply;
const counts = { list: 0, detail: 0, wallet: 0, services: 0, posts: 0 };

const respond = ({ status, body }: Reply) =>
  Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  Object.assign(counts, { list: 0, detail: 0, wallet: 0, services: 0, posts: 0 });
  bookingStatus = 'queued';
  listReply = () => ({ status: 200, body: { items: [booking(bookingStatus)], page: 1, pageSize: 20, hasNextPage: false } });
  onlineManager.setOnline(true);
  focusManager.setFocused(true);
  globalThis.fetch = jest.fn((url: string | URL | Request, init?: RequestInit) => {
    const target = String(url);
    if (init?.method === 'POST') {
      counts.posts++;
      return respond({ status: 500, body: {} });
    }
    if (target.endsWith('/wallet')) {
      counts.wallet++;
      return respond({ status: 200, body: { balanceCents: 3110, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' } });
    }
    if (target.endsWith('/services')) {
      counts.services++;
      return respond({ status: 200, body: { services: [] } });
    }
    if (/\/bookings\?/.test(target)) return respond(listReply(++counts.list));
    counts.detail++;
    return respond({ status: 200, body: booking(bookingStatus) });
  }) as unknown as typeof fetch;
});

afterEach(() => {
  onlineManager.setOnline(true);
  focusManager.setFocused(true);
  queryClient.clear();
});

const advance = (ms: number) => act(async () => jest.advanceTimersByTimeAsync(ms));

async function openServices() {
  const app = await renderRouter(APP_DIR);
  await fireEvent.press(screen.getByLabelText('Serviços, tab, 1 of 3'));
  await screen.findByRole('button', { name: /^Abrir contratação/ });
  return app;
}

describe('polling the bookings list', () => {
  it('refreshes every 10 seconds while the screen is visible and an item is not terminal', async () => {
    await openServices();
    expect(counts.list).toBe(1);

    await advance(9_900);
    expect(counts.list).toBe(1);
    await advance(100);
    expect(counts.list).toBe(2);
    await advance(10_000);
    expect(counts.list).toBe(3);
  });

  it('stops when the screen loses focus and keeps going when it returns', async () => {
    await openServices();
    await advance(10_000);
    expect(counts.list).toBe(2);

    await fireEvent.press(screen.getByLabelText('Home, tab, 2 of 3'));
    await advance(60_000);
    expect(counts.list).toBe(2);

    await fireEvent.press(screen.getByLabelText('Serviços, tab, 1 of 3'));
    await advance(10_000);
    expect(counts.list).toBeGreaterThan(2);
  });

  it('stops in the background and re-reads when the app comes back, with no POST', async () => {
    await openServices();
    await advance(10_000);
    const before = counts.list;

    await act(async () => focusManager.setFocused(false));
    await advance(60_000);
    expect(counts.list).toBe(before);

    await act(async () => focusManager.setFocused(true));
    await advance(0);
    expect(counts.list).toBeGreaterThan(before);
    expect(counts.posts).toBe(0);
  });

  it('stops offline and re-reads when the connection returns', async () => {
    await openServices();
    await advance(10_000);
    const before = counts.list;

    await act(async () => onlineManager.setOnline(false));
    await advance(60_000);
    expect(counts.list).toBe(before);

    await act(async () => onlineManager.setOnline(true));
    await advance(0);
    expect(counts.list).toBeGreaterThan(before);
  });

  it('does not poll when every booking is terminal', async () => {
    bookingStatus = 'completed';
    await renderRouter(APP_DIR);
    await fireEvent.press(screen.getByLabelText('Serviços, tab, 1 of 3'));
    await screen.findByRole('radio', { name: 'Histórico' });
    await fireEvent.press(screen.getByRole('radio', { name: 'Histórico' }));
    expect(counts.list).toBe(1);

    await advance(60_000);

    expect(counts.list).toBe(1);
  });

  it('never polls the wallet by reflex nor posts anything while polling the list', async () => {
    await openServices();
    const wallet = counts.wallet;
    const services = counts.services;

    await advance(60_000);

    expect(counts.list).toBeGreaterThanOrEqual(6);
    expect(counts.wallet).toBe(wallet);
    expect(counts.services).toBe(services);
    expect(counts.posts).toBe(0);
  });

  it('stops polling after the server rate limits the list', async () => {
    listReply = (call) =>
      call < 2
        ? { status: 200, body: { items: [booking('queued')], page: 1, pageSize: 20, hasNextPage: false } }
        : { status: 429, body: { errorCode: 'RATE_LIMIT_EXCEEDED' } };
    await openServices();

    await advance(10_000);
    expect(counts.list).toBe(2);
    await advance(60_000);

    expect(counts.list).toBe(2);
  });

  it('leaves no timer behind after unmount', async () => {
    const view = await openServices();
    await advance(10_000);
    await view.unmount();
    const before = { ...counts };

    await advance(60_000);

    expect(counts).toEqual(before);
  });
});

describe('polling the booking detail', () => {
  const openDetail = () => renderRouter(APP_DIR, { initialUrl: `/bookings/${BOOKING_ID}` });

  it('refreshes every 5 seconds while the item is not terminal and stops once it is', async () => {
    await openDetail();
    await screen.findByRole('header', { name: 'Contratação 00000000' });
    expect(counts.detail).toBe(1);

    await advance(5_000);
    expect(counts.detail).toBe(2);

    bookingStatus = 'completed';
    await advance(5_000);
    expect(counts.detail).toBe(3);
    await screen.findByText('Concluído');
    await advance(60_000);
    expect(counts.detail).toBe(3);
  });

  it('does not poll a malformed id and does not poll in the background', async () => {
    await renderRouter(APP_DIR, { initialUrl: '/bookings/abc' });
    await advance(30_000);
    expect(counts.detail).toBe(0);

    await openDetail();
    await screen.findByRole('header', { name: 'Contratação 00000000' });
    const before = counts.detail;
    await act(async () => focusManager.setFocused(false));
    await advance(30_000);

    expect(counts.detail).toBe(before);
  });
});
