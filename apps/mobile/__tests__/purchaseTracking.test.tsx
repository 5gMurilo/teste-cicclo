import { fireEvent, renderRouter, screen, waitFor } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

jest.mock('../src/shared/api/capabilities', () => ({ capabilities: { purchase: true, keyRetentionMs: null } }));
jest.mock('expo-crypto', () => ({ randomUUID: () => '00000000-0000-4000-8000-0000000000aa' }));

const APP_DIR = './src/app';
const nbsp = (text: string) => text.replace(/R\$ /g, 'R$ ');
const BOOKING_ID = '11111111-1111-4111-8111-111111111111';

const booking = {
  bookingId: BOOKING_ID,
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

let purchased: boolean;
let posts: number;
let walletFails: boolean;
let detailGets: number;

const respond = (status: number, body: unknown) =>
  Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  purchased = false;
  posts = 0;
  walletFails = false;
  detailGets = 0;
  globalThis.fetch = jest.fn((url: string | URL | Request, init?: RequestInit) => {
    const target = String(url);
    if (init?.method === 'POST') {
      posts++;
      purchased = true;
      return respond(201, booking);
    }
    if (target.endsWith('/wallet')) {
      return walletFails && purchased
        ? respond(422, { errorCode: 'BOOM' })
        : respond(200, { balanceCents: purchased ? 3110 : 5000, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' });
    }
    if (target.endsWith('/services')) {
      return respond(200, {
        services: [
          { code: 'wash', name: 'Lavagem', machineType: 'washer', unitPriceCents: 1890, currency: 'BRL', isActive: true, canBeBooked: true, unavailabilityReason: null },
        ],
      });
    }
    if (/\/bookings\?/.test(target)) {
      return respond(200, { items: purchased ? [booking] : [], page: 1, pageSize: 20, hasNextPage: false });
    }
    detailGets++;
    return respond(200, booking);
  }) as unknown as typeof fetch;
});

afterEach(() => queryClient.clear());

async function purchaseWash() {
  const app = await renderRouter(APP_DIR);
  await screen.findByText('Lavagem');
  await screen.findByText(nbsp('R$ 50,00'));
  await fireEvent.press(screen.getByLabelText('Serviços, tab, 1 of 3'));
  await screen.findByText('Nenhuma contratação em andamento.');
  await fireEvent.press(screen.getByLabelText('Home, tab, 2 of 3'));
  await fireEvent.press(screen.getByRole('button', { name: /^Adicionar Lavagem/ }));
  await fireEvent.press(screen.getByLabelText(/^Checkout.*tab, 3 of 3$/));
  await fireEvent.press(screen.getByRole('button', { name: /^Confirmar contratação/ }));
  await screen.findByText('Contratação confirmada');
  return app;
}

describe('after a confirmed purchase', () => {
  it('leads to the real booking detail', async () => {
    const app = await purchaseWash();

    await fireEvent.press(screen.getByRole('button', { name: 'Ver contratação' }));

    expect(app.getPathname()).toBe(`/bookings/${BOOKING_ID}`);
    expect(await screen.findByRole('header', { name: 'Contratação 11111111' })).toBeOnTheScreen();
    expect(screen.getByText('Posição na fila: 1')).toBeOnTheScreen();
    expect(detailGets).toBeGreaterThan(0);
  });

  it('makes the new booking appear in Serviços after the invalidation and keeps the confirmed balance on return', async () => {
    const app = await purchaseWash();

    await fireEvent.press(screen.getByRole('button', { name: 'Ver serviços' }));
    expect(app.getPathname()).toBe('/services');
    expect(await screen.findByRole('button', { name: /^Abrir contratação 11111111/ })).toBeOnTheScreen();

    await fireEvent.press(screen.getByLabelText('Home, tab, 2 of 3'));
    expect(await screen.findByLabelText(`Saldo: ${nbsp('R$ 31,10')}`)).toBeOnTheScreen();
    expect(posts).toBe(1);
  });

  it('stays confirmed and does not post again when the read that follows fails', async () => {
    walletFails = true;
    await renderRouter(APP_DIR);
    await screen.findByText('Lavagem');
    await screen.findByText(nbsp('R$ 50,00'));
    await fireEvent.press(screen.getByRole('button', { name: /^Adicionar Lavagem/ }));
    await fireEvent.press(screen.getByLabelText(/^Checkout.*tab, 3 of 3$/));
    await fireEvent.press(screen.getByRole('button', { name: /^Confirmar contratação/ }));

    expect(await screen.findByText('Contratação confirmada')).toBeOnTheScreen();
    await waitFor(() => expect(screen.getByText(/^Desatualizado/)).toBeOnTheScreen());

    expect(screen.getByText('Contratação confirmada')).toBeOnTheScreen();
    expect(posts).toBe(1);
  });
});
