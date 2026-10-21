import { fireEvent, renderRouter, screen } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

// This file covers the detail with the item commands off; with them on, see itemCommands.test.tsx.
jest.mock('../src/shared/api/capabilities', () => ({
  capabilities: {
    purchase: true,
    keyRetentionMs: null,
    itemCommands: { activate: false, confirm: false, cancel: false, reportIssue: false },
  },
}));

const APP_DIR = './src/app';
const nbsp = (text: string) => text.replace(/R\$ /g, 'R$ ');
const ID = '00000000-0000-4000-8000-000000000001';

const item = (n: number, serviceCode: string, status: string, extra: Record<string, unknown> = {}) => ({
  itemId: `10000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  serviceCode,
  unitPriceCents: 1500,
  status,
  machineId: null,
  queuePosition: null,
  expiresAt: null,
  ...extra,
});
const booking = (items: unknown[]) => ({
  bookingId: ID,
  currency: 'BRL',
  chargedAmountCents: 3000,
  remainingBalanceCents: 2000,
  createdAt: '2026-10-21T12:00:00Z',
  items,
});

type Reply = { status: number; body: unknown };
let detailReplies: Reply[];
const requested: string[] = [];

const respond = ({ status, body }: Reply) =>
  Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  requested.length = 0;
  detailReplies = [];
  globalThis.fetch = jest.fn((url: string | URL | Request, init?: RequestInit) => {
    const target = String(url);
    requested.push(`${init?.method ?? 'GET'} ${target}`);
    if (target.endsWith('/wallet')) return respond({ status: 200, body: { balanceCents: 5000, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' } });
    if (target.endsWith('/services')) return respond({ status: 200, body: { services: [] } });
    if (/\/bookings\?/.test(target)) return respond({ status: 200, body: { items: [booking([item(1, 'wash', 'queued')])], page: 1, pageSize: 20, hasNextPage: false } });
    return respond(detailReplies.length > 1 ? (detailReplies.shift() as Reply) : (detailReplies[0] ?? { status: 500, body: {} }));
  }) as unknown as typeof fetch;
});

afterEach(() => queryClient.clear());

const openDetail = (id: string) => renderRouter(APP_DIR, { initialUrl: `/bookings/${id}` });
const detailGets = () => requested.filter((r) => /\/bookings\/[^?]/.test(r));

describe('booking details', () => {
  it('shows each item with its historical price, status, queue position, offer deadline and simulated machine', async () => {
    detailReplies = [
      {
        status: 200,
        body: booking([
          item(1, 'wash', 'queued', { queuePosition: 2 }),
          item(2, 'dry', 'to_be_confirmed', {
            machineId: '20000000-0000-4000-8000-000000000009',
            expiresAt: '2026-10-21T12:01:00Z',
          }),
          item(3, 'wash', 'completed'),
        ]),
      },
    ];

    await openDetail(ID);

    expect(screen.getByLabelText('Carregando contratação')).toBeOnTheScreen();
    expect(await screen.findByRole('header', { name: 'Contratação 00000000' })).toBeOnTheScreen();
    expect(screen.getByText(`Valor cobrado: ${nbsp('R$ 30,00')}`)).toBeOnTheScreen();
    expect(screen.getAllByText(`wash · ${nbsp('R$ 15,00')}`)).toHaveLength(2);
    expect(screen.getByText('Na fila')).toBeOnTheScreen();
    expect(screen.getByText('Posição na fila: 2')).toBeOnTheScreen();
    expect(screen.getByText('Máquina ofertada, aguardando confirmação')).toBeOnTheScreen();
    expect(screen.getByText(/^Oferta válida até /)).toBeOnTheScreen();
    expect(screen.getByText('Máquina simulada 20000000')).toBeOnTheScreen();
    expect(screen.getByText('Concluído')).toBeOnTheScreen();
    expect(screen.getByText('As máquinas são simuladas.')).toBeOnTheScreen();
  });

  it('offers no action beyond going back while the item commands are not enabled', async () => {
    detailReplies = [{ status: 200, body: booking([item(1, 'wash', 'to_be_confirmed', { expiresAt: '2026-10-21T12:01:00Z', machineId: '20000000-0000-4000-8000-000000000009' }), item(2, 'dry', 'pending_activation')]) }];

    await openDetail(ID);
    await screen.findByRole('header', { name: 'Contratação 00000000' });

    expect(screen.getAllByRole('button').map((b) => b.props.accessibilityLabel)).toEqual(['Voltar para Serviços']);
    expect(requested.some((r) => r.startsWith('POST'))).toBe(false);
  });

  it('never claims a refund for cancelled or failed items', async () => {
    detailReplies = [{ status: 200, body: booking([item(1, 'wash', 'cancelled'), item(2, 'dry', 'failed')]) }];

    await openDetail(ID);
    await screen.findByText('Cancelado');

    expect(screen.getByText('Falhou')).toBeOnTheScreen();
    expect(screen.queryByText(/estorno|reembolso|devolv/i)).toBeNull();
  });

  it('shows an unrecognized status as such', async () => {
    detailReplies = [{ status: 200, body: booking([item(1, 'wash', 'teleported')]) }];

    await openDetail(ID);

    expect(await screen.findByText('Estado não reconhecido')).toBeOnTheScreen();
  });

  it.each(['abc', '123'])('shows a recoverable error for the invalid id %p without any detail request', async (id) => {
    const app = await openDetail(id);

    expect(screen.getByText('Identificador de contratação inválido.')).toBeOnTheScreen();
    expect(detailGets()).toHaveLength(0);

    await fireEvent.press(screen.getByRole('button', { name: 'Voltar para Serviços' }));
    expect(app.getPathname()).toBe('/services');
  });

  it('shows not found for a missing booking and goes back to Serviços', async () => {
    detailReplies = [{ status: 404, body: { errorCode: 'BOOKING_NOT_FOUND' } }];
    const app = await openDetail(ID);

    expect(await screen.findByText('Contratação não encontrada.')).toBeOnTheScreen();
    expect(screen.queryByRole('button', { name: 'Tentar novamente' })).toBeNull();
    await fireEvent.press(screen.getByRole('button', { name: 'Voltar para Serviços' }));
    expect(app.getPathname()).toBe('/services');
  });

  it('retries a failed load and recovers', async () => {
    detailReplies = [{ status: 422, body: { errorCode: 'BOOM' } }, { status: 200, body: booking([item(1, 'wash', 'queued')]) }];
    await openDetail(ID);

    expect(await screen.findByText('Não foi possível carregar a contratação.')).toBeOnTheScreen();
    await fireEvent.press(screen.getByRole('button', { name: 'Tentar novamente' }));

    expect(await screen.findByText('Na fila')).toBeOnTheScreen();
  });

  it('returns to Serviços without touching the checkout selection', async () => {
    detailReplies = [{ status: 200, body: booking([item(1, 'wash', 'queued')]) }];
    globalThis.fetch = jest.fn((url: string | URL | Request) => {
      const target = String(url);
      if (target.endsWith('/wallet')) return respond({ status: 200, body: { balanceCents: 5000, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' } });
      if (target.endsWith('/services')) {
        return respond({
          status: 200,
          body: { services: [{ code: 'wash', name: 'Lavagem', machineType: 'washer', unitPriceCents: 1890, currency: 'BRL', isActive: true, canBeBooked: true, unavailabilityReason: null }] },
        });
      }
      if (/\/bookings\?/.test(target)) return respond({ status: 200, body: { items: [booking([item(1, 'wash', 'queued')])], page: 1, pageSize: 20, hasNextPage: false } });
      return respond(detailReplies[0]);
    }) as unknown as typeof fetch;
    await renderRouter(APP_DIR);
    await screen.findByText('Lavagem');
    await screen.findByText(nbsp('R$ 50,00'));
    await fireEvent.press(screen.getByRole('button', { name: /^Adicionar Lavagem/ }));

    await fireEvent.press(screen.getByLabelText('Serviços, tab, 1 of 3'));
    await fireEvent.press(await screen.findByRole('button', { name: /^Abrir contratação/ }));
    await screen.findByRole('header', { name: 'Contratação 00000000' });
    await fireEvent.press(screen.getByRole('button', { name: 'Voltar para Serviços' }));

    expect(screen.getByLabelText('Checkout, 1 item, tab, 3 of 3')).toBeOnTheScreen();
  });
});
