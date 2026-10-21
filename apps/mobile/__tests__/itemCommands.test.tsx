import { fireEvent, renderRouter, screen, waitFor } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

// Item commands are gated by capabilities proven against the real API; these tests exercise the enabled path.
jest.mock('../src/shared/api/capabilities', () => ({
  capabilities: {
    purchase: true,
    keyRetentionMs: null,
    itemCommands: { activate: true, confirm: true, cancel: true, reportIssue: true },
  },
}));

const APP_DIR = './src/app';
const BOOKING_ID = '00000000-0000-4000-8000-000000000001';
const ITEM_ID = '10000000-0000-4000-8000-000000000001';
const MACHINE_ID = '20000000-0000-4000-8000-000000000009';

type Item = {
  itemId: string;
  serviceCode: string;
  unitPriceCents: number;
  status: string;
  machineId: string | null;
  queuePosition: number | null;
  expiresAt: string | null;
};
const item = (status: string, over: Partial<Item> = {}): Item => ({
  itemId: ITEM_ID,
  serviceCode: 'wash',
  unitPriceCents: 1890,
  status,
  machineId: null,
  queuePosition: null,
  expiresAt: null,
  ...over,
});
const booking = (...items: Item[]) => ({
  bookingId: BOOKING_ID,
  currency: 'BRL',
  chargedAmountCents: 1890 * items.length,
  remainingBalanceCents: 3110,
  createdAt: '2026-10-21T12:00:00Z',
  items,
});

type Reply = { status: number; body: unknown; headers?: Record<string, string> };
let detail: Reply;
let posts: { url: string; body: string | undefined }[];
let commandReply: (url: string) => Reply | Promise<Reply>;
let gets: number;

function response({ status, body, headers = {} }: Reply): Response {
  const lower = Object.fromEntries(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), v]));
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: { get: (name: string) => lower[name.toLowerCase()] ?? null },
    text: async () => JSON.stringify(body),
  } as unknown as Response;
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  posts = [];
  gets = 0;
  detail = { status: 200, body: booking(item('pending_activation')) };
  commandReply = () => ({ status: 200, body: item('queued', { queuePosition: 1 }) });
  globalThis.fetch = jest.fn(async (url: string | URL | Request, init?: RequestInit) => {
    const target = String(url);
    if (init?.method === 'POST') {
      posts.push({ url: target, body: init.body as string | undefined });
      return response(await commandReply(target));
    }
    if (target.endsWith('/wallet')) {
      return response({ status: 200, body: { balanceCents: 3110, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' } });
    }
    if (target.endsWith('/services')) return response({ status: 200, body: { services: [] } });
    if (/\/bookings\?/.test(target)) return response({ status: 200, body: { items: [], page: 1, pageSize: 20, hasNextPage: false } });
    gets++;
    return response(detail);
  }) as unknown as typeof fetch;
});

afterEach(() => queryClient.clear());

const openDetail = () => renderRouter(APP_DIR, { initialUrl: `/bookings/${BOOKING_ID}` });
const commandUrl = (action: string) => `http://api.test:5001/api/v1/bookings/${BOOKING_ID}/items/${ITEM_ID}/${action}`;

describe('T31: activate a paid pending item', () => {
  it('offers Ativar only for pending_activation and posts to the item activate endpoint', async () => {
    await openDetail();
    await screen.findByText('Paga, aguardando ativação');

    await fireEvent.press(screen.getByRole('button', { name: 'Ativar wash' }));

    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0].url).toBe(commandUrl('activate'));
  });

  it('shows the server state after activation: queued says it is already paid, with no new charge text', async () => {
    await openDetail();
    await screen.findByText('Paga, aguardando ativação');
    detail = { status: 200, body: booking(item('queued', { queuePosition: 2 })) };

    await fireEvent.press(screen.getByRole('button', { name: 'Ativar wash' }));

    expect(await screen.findByText('Na fila')).toBeOnTheScreen();
    expect(screen.getByText('Posição na fila: 2')).toBeOnTheScreen();
    expect(screen.getByText('Já pago: o valor foi cobrado na contratação.')).toBeOnTheScreen();
    expect(screen.queryByRole('button', { name: 'Ativar wash' })).toBeNull();
  });

  it.each(['queued', 'to_be_confirmed', 'in_progress', 'completed', 'cancelled', 'failed'])(
    'never offers or sends activate for an item that is %s',
    async (status) => {
      detail = { status: 200, body: booking(item(status, status === 'to_be_confirmed' ? { machineId: MACHINE_ID, expiresAt: '2099-01-01T00:00:00Z' } : {})) };
      await openDetail();
      await screen.findByRole('header', { name: 'Contratação 00000000' });

      expect(screen.queryByRole('button', { name: 'Ativar wash' })).toBeNull();
      expect(posts).toHaveLength(0);
    },
  );

  it('refetches and explains on a 409, showing the real state instead of an assumed one', async () => {
    commandReply = () => ({ status: 409, body: { errorCode: 'INVALID_STATE_TRANSITION' } });
    await openDetail();
    await screen.findByText('Paga, aguardando ativação');
    const before = gets;
    detail = { status: 200, body: booking(item('cancelled')) };

    await fireEvent.press(screen.getByRole('button', { name: 'Ativar wash' }));

    expect(await screen.findByText(/O estado da execução mudou/)).toBeOnTheScreen();
    await waitFor(() => expect(screen.getByText('Cancelado')).toBeOnTheScreen());
    expect(gets).toBeGreaterThan(before);
    expect(screen.queryByRole('button', { name: 'Ativar wash' })).toBeNull();
  });

  it('does not duplicate the command while one is pending', async () => {
    let finish: (reply: Reply) => void = () => undefined;
    commandReply = () => new Promise<Reply>((resolve) => (finish = resolve));
    await openDetail();
    await screen.findByText('Paga, aguardando ativação');

    await fireEvent.press(screen.getByRole('button', { name: 'Ativar wash' }));
    await fireEvent.press(screen.getByRole('button', { name: 'Ativar wash' }));

    expect(posts).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Ativar wash' })).toBeDisabled();
    finish({ status: 200, body: item('queued', { queuePosition: 1 }) });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ativar wash' })).toBeEnabled());
  });
});

describe('T33: confirm an eligible machine offer', () => {
  const OFFER_SECONDS = 60;
  let clock: number;
  const offer = (secondsLeft: number) =>
    item('to_be_confirmed', { machineId: MACHINE_ID, expiresAt: new Date(clock + secondsLeft * 1000).toISOString() });

  beforeEach(() => {
    clock = Date.parse('2026-10-21T12:00:00Z');
    jest.spyOn(Date, 'now').mockImplementation(() => clock);
    detail = { status: 200, body: booking(offer(OFFER_SECONDS)) };
  });

  afterEach(() => jest.restoreAllMocks());

  it('shows the countdown and confirms the current offer; the status comes from the server, not from the click', async () => {
    commandReply = () => ({ status: 200, body: item('in_progress', { machineId: MACHINE_ID }) });
    await openDetail();
    expect(await screen.findByText('Tempo restante: 60 s')).toBeOnTheScreen();
    expect(screen.getByText('Máquina ofertada, aguardando confirmação')).toBeOnTheScreen();

    await fireEvent.press(screen.getByRole('button', { name: 'Confirmar wash' }));
    expect(screen.getByText('Máquina ofertada, aguardando confirmação')).toBeOnTheScreen();

    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0].url).toBe(commandUrl('confirm'));
    detail = { status: 200, body: booking(item('in_progress', { machineId: MACHINE_ID })) };
    await queryClient.invalidateQueries({ queryKey: ['bookings'] });
    expect(await screen.findByText('Em execução')).toBeOnTheScreen();
    expect(screen.queryByRole('button', { name: 'Confirmar wash' })).toBeNull();
  });

  it('sends nothing when the deadline has already been reached on the local clock', async () => {
    detail = { status: 200, body: booking(offer(0)) };
    await openDetail();
    await screen.findByText('Oferta vencida');

    expect(screen.queryByRole('button', { name: 'Confirmar wash' })).toBeNull();
    expect(posts).toHaveLength(0);
  });

  it('does not send when the offer ran out while the app was away and the timer never fired', async () => {
    await openDetail();
    await screen.findByText('Tempo restante: 60 s');
    // renderRouter installs Jest fake timers, so the clock is moved through Jest, not through a Date spy.
    jest.setSystemTime(Date.now() + (OFFER_SECONDS + 1) * 1000);

    await fireEvent.press(screen.getByRole('button', { name: 'Confirmar wash' }));

    expect(posts).toHaveLength(0);
    expect(screen.queryByRole('button', { name: 'Confirmar wash' })).toBeNull();
  });

  it('respects a 410 even with time left on the local clock, refetches and explains', async () => {
    commandReply = () => ({ status: 410, body: { errorCode: 'MACHINE_OFFER_EXPIRED' } });
    await openDetail();
    await screen.findByText('Tempo restante: 60 s');
    const before = gets;
    detail = { status: 200, body: booking(item('queued', { queuePosition: 1 })) };

    await fireEvent.press(screen.getByRole('button', { name: 'Confirmar wash' }));

    expect(await screen.findByText(/A oferta venceu e a execução voltou para a fila/)).toBeOnTheScreen();
    await waitFor(() => expect(screen.getByText('Na fila')).toBeOnTheScreen());
    expect(gets).toBeGreaterThan(before);
  });

  it('refetches on a 409 and shows the real state', async () => {
    commandReply = () => ({ status: 409, body: { errorCode: 'INVALID_STATE_TRANSITION' } });
    await openDetail();
    await screen.findByText('Tempo restante: 60 s');
    detail = { status: 200, body: booking(item('queued', { queuePosition: 3 })) };

    await fireEvent.press(screen.getByRole('button', { name: 'Confirmar wash' }));

    expect(await screen.findByText(/O estado da execução mudou/)).toBeOnTheScreen();
    await waitFor(() => expect(screen.getByText('Posição na fila: 3')).toBeOnTheScreen());
  });

  it('sends a double tap only once', async () => {
    let finish: (reply: Reply) => void = () => undefined;
    commandReply = () => new Promise<Reply>((resolve) => (finish = resolve));
    await openDetail();
    await screen.findByText('Tempo restante: 60 s');

    await fireEvent.press(screen.getByRole('button', { name: 'Confirmar wash' }));
    await fireEvent.press(screen.getByRole('button', { name: 'Confirmar wash' }));

    expect(posts).toHaveLength(1);
    finish({ status: 200, body: item('in_progress', { machineId: MACHINE_ID }) });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Confirmar wash' })).toBeEnabled());
  });
});
