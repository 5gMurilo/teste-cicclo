import { fireEvent, renderRouter, screen, waitFor } from 'expo-router/testing-library';
import { Text } from 'react-native';

import { queryClient } from '../src/shared/api/queryClient';

const APP_DIR = './src/app';
const SERVICES_TAB = 'Serviços, tab, 1 of 3';
const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

const item = (n: number, status: string, serviceCode = 'wash') => ({
  itemId: `10000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  serviceCode,
  unitPriceCents: 1890,
  status,
  machineId: null,
  queuePosition: null,
  expiresAt: null,
});
const booking = (n: number, createdAt: string, ...statuses: string[]) => ({
  bookingId: id(n),
  currency: 'BRL',
  chargedAmountCents: 1890 * statuses.length,
  remainingBalanceCents: 3110,
  createdAt,
  items: statuses.map((status, i) => item(n * 10 + i, status, i === 0 ? 'wash' : 'dry')),
});
const page = (items: unknown[], pageNumber = 1, hasNextPage = false) => ({ items, page: pageNumber, pageSize: 20, hasNextPage });

type Reply = { status: number; body: unknown };
let listReplies: ((url: string) => Reply)[];
const requested: string[] = [];

const respond = ({ status, body }: Reply) =>
  Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);

function serve(...replies: ((url: string) => Reply)[]) {
  listReplies = replies;
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  requested.length = 0;
  globalThis.fetch = jest.fn((url: string | URL | Request, init?: RequestInit) => {
    const target = String(url);
    requested.push(`${init?.method ?? 'GET'} ${target}`);
    if (target.endsWith('/wallet')) return respond({ status: 200, body: { balanceCents: 5000, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' } });
    if (target.endsWith('/services')) return respond({ status: 200, body: { services: [] } });
    const next = listReplies.length > 1 ? listReplies.shift() : listReplies[0];
    return respond(next ? next(target) : { status: 500, body: {} });
  }) as unknown as typeof fetch;
});

afterEach(() => queryClient.clear());

async function openServices() {
  const app = await renderRouter({ appDir: APP_DIR, overrides: { 'bookings/[id]': () => <Text>detail</Text> } });
  await fireEvent.press(screen.getByLabelText(SERVICES_TAB));
  return app;
}

describe('Serviços screen', () => {
  it('shows loading, then active bookings newest first, with a readable id, date, total and item status', async () => {
    serve(() => ({
      status: 200,
      body: page([
        booking(1, '2026-10-20T09:00:00Z', 'queued'),
        booking(2, '2026-10-21T12:00:00Z', 'to_be_confirmed'),
      ]),
    }));

    await openServices();
    expect(screen.getByLabelText('Carregando contratações')).toBeOnTheScreen();

    const cards = await screen.findAllByRole('button', { name: /^Abrir contratação/ });
    expect(cards.map((c) => c.props.accessibilityLabel.slice(0, 32))).toEqual([
      'Abrir contratação 00000000, 21/10',
      'Abrir contratação 00000000, 20/10',
    ].map((s) => s.slice(0, 32)));
    expect(screen.getAllByText(/Máquina ofertada, aguardando confirmação|Na fila/)).toHaveLength(2);
    expect(requested.some((r) => r.startsWith('POST'))).toBe(false);
  });

  it('keeps finished items of a mixed batch in the active summary and moves only fully finished bookings to history', async () => {
    serve(() => ({
      status: 200,
      body: page([
        booking(1, '2026-10-21T12:00:00Z', 'completed', 'queued'),
        booking(2, '2026-10-20T09:00:00Z', 'completed', 'cancelled'),
      ]),
    }));
    await openServices();

    await screen.findAllByRole('button', { name: /^Abrir contratação/ });
    expect(screen.getAllByRole('button', { name: /^Abrir contratação/ })).toHaveLength(1);
    expect(screen.getByText(/Concluído/)).toBeOnTheScreen();
    expect(screen.getByText(/Na fila/)).toBeOnTheScreen();

    await fireEvent.press(screen.getByRole('radio', { name: 'Histórico' }));
    expect(screen.getAllByRole('button', { name: /^Abrir contratação/ })).toHaveLength(1);
    expect(screen.getByText(/Cancelado/)).toBeOnTheScreen();
  });

  it('shows a different empty state for each filter, both with a way back to Home', async () => {
    serve(() => ({ status: 200, body: page([booking(1, '2026-10-21T12:00:00Z', 'completed')]) }));
    const app = await openServices();

    expect(await screen.findByText('Nenhuma contratação em andamento.')).toBeOnTheScreen();
    await fireEvent.press(screen.getByRole('radio', { name: 'Histórico' }));
    expect(screen.queryByText('Nenhuma contratação no histórico.')).toBeNull();
    await fireEvent.press(screen.getByRole('radio', { name: 'Em andamento' }));

    serve(() => ({ status: 200, body: page([]) }));
    await fireEvent.press(screen.getByRole('button', { name: 'Escolher serviços' }));
    expect(app.getPathname()).toBe('/');
  });

  it('shows the History empty state with a way back to Home when only active bookings exist', async () => {
    serve(() => ({ status: 200, body: page([booking(1, '2026-10-21T12:00:00Z', 'queued')]) }));
    const app = await openServices();
    await screen.findByRole('button', { name: /^Abrir contratação/ });

    await fireEvent.press(screen.getByRole('radio', { name: 'Histórico' }));

    expect(screen.getByText('Nenhuma contratação no histórico.')).toBeOnTheScreen();
    expect(screen.queryByRole('button', { name: /^Abrir contratação/ })).toBeNull();
    await fireEvent.press(screen.getByRole('button', { name: 'Escolher serviços' }));
    expect(app.getPathname()).toBe('/');
  });

  it('shows an error with retry and recovers', async () => {
    serve(
      () => ({ status: 422, body: { errorCode: 'BOOM' } }),
      () => ({ status: 200, body: page([booking(1, '2026-10-21T12:00:00Z', 'queued')]) }),
    );
    await openServices();

    expect(await screen.findByText('Não foi possível carregar suas contratações.')).toBeOnTheScreen();
    await fireEvent.press(screen.getByRole('button', { name: 'Tentar novamente' }));

    expect(await screen.findByRole('button', { name: /^Abrir contratação/ })).toBeOnTheScreen();
  });

  it('states the limitation honestly when the endpoint does not exist, with no fake empty list', async () => {
    serve(() => ({ status: 404, body: { errorCode: 'NOT_FOUND' } }));
    await openServices();

    expect(await screen.findByText('A listagem de contratações não está disponível nesta versão da API.')).toBeOnTheScreen();
    expect(screen.queryByText('Nenhuma contratação em andamento.')).toBeNull();
  });

  it('does not claim completeness when more pages exist, and loads the next page on demand', async () => {
    serve(
      () => ({ status: 200, body: page([booking(2, '2026-10-21T12:00:00Z', 'queued')], 1, true) }),
      () => ({ status: 200, body: page([booking(1, '2026-10-20T09:00:00Z', 'queued')], 2, false) }),
    );
    await openServices();

    expect(await screen.findByText('Mostrando as contratações carregadas. Há mais registros.')).toBeOnTheScreen();
    await fireEvent.press(screen.getByRole('button', { name: 'Carregar mais' }));

    await waitFor(() => expect(screen.getAllByRole('button', { name: /^Abrir contratação/ })).toHaveLength(2));
    expect(requested.filter((r) => r.includes('/bookings?')).map((r) => r.split('?')[1])).toEqual([
      'page=1&pageSize=20',
      'page=2&pageSize=20',
    ]);
    expect(screen.queryByText('Mostrando as contratações carregadas. Há mais registros.')).toBeNull();
  });

  it('opens the detail route with the real booking id', async () => {
    serve(() => ({ status: 200, body: page([booking(7, '2026-10-21T12:00:00Z', 'queued')]) }));
    const app = await openServices();

    await fireEvent.press(await screen.findByRole('button', { name: /^Abrir contratação 00000000/ }));

    expect(app.getPathname()).toBe(`/bookings/${id(7)}`);
  });
});
