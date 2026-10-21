import { act } from '@testing-library/react-native';
import { fireEvent, renderRouter, screen, waitFor } from 'expo-router/testing-library';

import { capabilities } from '../src/shared/api/capabilities';
import { queryClient } from '../src/shared/api/queryClient';

// Item commands are gated by capabilities proven against the real API; these tests exercise the enabled path.
jest.mock('../src/shared/api/capabilities', () => ({
  capabilities: {
    purchase: true,
    keyRetentionMs: null,
    itemCommands: { activate: true, confirm: true, cancel: true, reportIssue: true }, simulation: { complete: true },
  },
}));

const APP_DIR = './src/app';
const nbsp = (text: string) => text.replace(/R\$ /g, 'R$\u00a0');
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
let walletBalance: number;
let walletGate: Promise<void>;

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
  walletBalance = 3110;
  walletGate = Promise.resolve();
  detail = { status: 200, body: booking(item('pending_activation')) };
  commandReply = () => ({ status: 200, body: item('queued', { queuePosition: 1 }) });
  globalThis.fetch = jest.fn(async (url: string | URL | Request, init?: RequestInit) => {
    const target = String(url);
    if (init?.method === 'POST') {
      posts.push({ url: target, body: init.body as string | undefined });
      return response(await commandReply(target));
    }
    if (target.endsWith('/wallet')) {
      await walletGate;
      return response({ status: 200, body: { balanceCents: walletBalance, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' } });
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

describe('T34: cancel before start, refund only when confirmed', () => {
  const cancelBody = (refundedAmountCents: number | undefined, remaining = 5000) => ({
    item: item('cancelled'),
    ...(refundedAmountCents === undefined ? {} : { refundedAmountCents }),
    remainingBalanceCents: remaining,
  });
  const startCancel = async () => {
    await fireEvent.press(screen.getByRole('button', { name: 'Cancelar wash' }));
    await fireEvent.press(screen.getByRole('button', { name: 'Confirmar cancelamento de wash' }));
  };

  it.each([
    ['pending_activation', {}],
    ['queued', { queuePosition: 1 }],
    ['to_be_confirmed', { machineId: MACHINE_ID, expiresAt: '2099-01-01T00:00:00Z' }],
  ])('offers cancel for an item that is %s', async (status, extra) => {
    detail = { status: 200, body: booking(item(status, extra)) };
    await openDetail();

    expect(await screen.findByRole('button', { name: 'Cancelar wash' })).toBeOnTheScreen();
  });

  it.each(['in_progress', 'completed', 'cancelled', 'failed'])('never offers cancel for an item that is %s', async (status) => {
    detail = { status: 200, body: booking(item(status, status === 'in_progress' ? { machineId: MACHINE_ID } : {})) };
    await openDetail();
    await screen.findByRole('header', { name: 'Contratação 00000000' });

    expect(screen.queryByRole('button', { name: /^Cancelar wash/ })).toBeNull();
    expect(posts).toHaveLength(0);
  });

  it('asks first, and keeping the item sends nothing', async () => {
    detail = { status: 200, body: booking(item('queued', { queuePosition: 1 })) };
    await openDetail();
    await screen.findByText('Na fila');

    await fireEvent.press(screen.getByRole('button', { name: 'Cancelar wash' }));
    expect(screen.getByText(/O estorno só é mostrado depois da confirmação do servidor/)).toBeOnTheScreen();
    await fireEvent.press(screen.getByRole('button', { name: 'Manter wash' }));

    expect(posts).toHaveLength(0);
    expect(screen.getByRole('button', { name: 'Cancelar wash' })).toBeOnTheScreen();
  });

  it('shows the refund the API confirmed and the balance it returned, only after the response', async () => {
    detail = { status: 200, body: booking(item('queued', { queuePosition: 1 })) };
    commandReply = () => {
      walletBalance = 5000;
      return { status: 200, body: cancelBody(1890, 5000) };
    };
    await openDetail();
    await screen.findByText('Na fila');
    await waitFor(() => expect(queryClient.getQueryData(['wallet'])).toMatchObject({ balanceCents: 3110 }));
    detail = { status: 200, body: booking(item('cancelled')) };

    await startCancel();

    expect(await screen.findByText(`Execução cancelada. Estorno confirmado: ${nbsp('R$ 18,90')}.`)).toBeOnTheScreen();
    expect(posts[0].url).toBe(commandUrl('cancel'));
    await waitFor(() => expect(screen.getByText('Cancelado')).toBeOnTheScreen());
    await waitFor(() => expect(queryClient.getQueryData(['wallet'])).toMatchObject({ balanceCents: 5000 }));
  });

  it('does not claim a refund when the response has no proof of one', async () => {
    detail = { status: 200, body: booking(item('queued', { queuePosition: 1 })) };
    commandReply = () => ({ status: 200, body: cancelBody(undefined) });
    await openDetail();
    await screen.findByText('Na fila');

    await startCancel();

    expect(await screen.findByText(/Não foi possível concluir a ação/)).toBeOnTheScreen();
    expect(screen.queryByText(/Estorno confirmado/)).toBeNull();
  });

  it('on an error gives no credit and shows the real state after the refetch', async () => {
    detail = { status: 200, body: booking(item('queued', { queuePosition: 1 })) };
    commandReply = () => ({ status: 409, body: { errorCode: 'INVALID_STATE_TRANSITION' } });
    await openDetail();
    await screen.findByText('Na fila');
    detail = { status: 200, body: booking(item('in_progress', { machineId: MACHINE_ID })) };

    await startCancel();

    expect(await screen.findByText(/O estado da execução mudou/)).toBeOnTheScreen();
    expect(queryClient.getQueryData(['wallet'])).toMatchObject({ balanceCents: 3110 });
    await waitFor(() => expect(screen.getByText('Em execução')).toBeOnTheScreen());
    expect(screen.queryByText(/Estorno/)).toBeNull();
    expect(screen.queryByRole('button', { name: /^Cancelar wash/ })).toBeNull();
  });

  it('shows no refund text when the API credited nothing', async () => {
    detail = { status: 200, body: booking(item('pending_activation')) };
    commandReply = () => ({ status: 200, body: cancelBody(0, 3110) });
    await openDetail();
    await screen.findByText('Paga, aguardando ativação');

    await startCancel();

    expect(await screen.findByText('Execução cancelada.')).toBeOnTheScreen();
    expect(screen.queryByText(/Estorno confirmado/)).toBeNull();
  });
});

describe('T35: report an issue without promising a refund', () => {
  const INCIDENT_ID = '019a0a3c-6f00-7000-8000-000000000001';

  beforeEach(() => {
    detail = { status: 200, body: booking(item('in_progress', { machineId: MACHINE_ID })) };
    commandReply = () => ({ status: 202, body: { incidentId: INCIDENT_ID, status: 'reported' } });
  });

  it('accepts the report: sends an empty POST, says it went for verification, mentions no refund, and cannot be repeated', async () => {
    await openDetail();

    await fireEvent.press(await screen.findByRole('button', { name: 'Relatar problema em wash' }));

    expect(await screen.findByText('Relato enviado para verificação.')).toBeOnTheScreen();
    expect(posts).toHaveLength(1);
    expect(posts[0].url).toBe(commandUrl('report-issue'));
    expect(posts[0].body).toBeUndefined();
    expect(screen.queryByText(/estorno|reembolso|devolv|crédito/i)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Relatar problema em wash' })).toBeNull();
    expect(queryClient.getQueryData(['wallet'])).toMatchObject({ balanceCents: 3110 });
  });

  it.each(['pending_activation', 'queued', 'to_be_confirmed', 'completed', 'cancelled', 'failed'])(
    'offers no report for an item that is %s',
    async (status) => {
      detail = { status: 200, body: booking(item(status, status === 'to_be_confirmed' ? { machineId: MACHINE_ID, expiresAt: '2099-01-01T00:00:00Z' } : {})) };
      await openDetail();
      await screen.findByRole('header', { name: 'Contratação 00000000' });

      expect(screen.queryByRole('button', { name: /^Relatar problema/ })).toBeNull();
    },
  );

  it('explains a 409 and shows the real state instead of recording anything', async () => {
    commandReply = () => ({ status: 409, body: { errorCode: 'INVALID_STATE_TRANSITION' } });
    await openDetail();
    await screen.findByText('Em execução');
    detail = { status: 200, body: booking(item('completed')) };

    await fireEvent.press(screen.getByRole('button', { name: 'Relatar problema em wash' }));

    expect(await screen.findByText(/O estado da execução mudou/)).toBeOnTheScreen();
    await waitFor(() => expect(screen.getByText('Concluído')).toBeOnTheScreen());
    expect(screen.queryByText('Relato enviado para verificação.')).toBeNull();
  });

  it('keeps the button usable after a rate limit, with the wait time', async () => {
    commandReply = () => ({ status: 429, body: { errorCode: 'RATE_LIMIT_EXCEEDED' }, headers: { 'Retry-After': '30' } });
    await openDetail();

    await fireEvent.press(await screen.findByRole('button', { name: 'Relatar problema em wash' }));

    expect(await screen.findByText(/Muitas tentativas\. Aguarde até/)).toBeOnTheScreen();
    expect(screen.getByRole('button', { name: 'Relatar problema em wash' })).toBeEnabled();
  });

  it('hides the action when the capability is not proven on the API', async () => {
    capabilities.itemCommands.reportIssue = false;
    try {
      await openDetail();
      await screen.findByText('Em execução');

      expect(screen.queryByRole('button', { name: /^Relatar problema/ })).toBeNull();
    } finally {
      capabilities.itemCommands.reportIssue = true;
    }
  });
});

describe('T34 and T33 hardening: nothing is credited early, the detail is re-read at the deadline', () => {
  const cancelBody = (refunded: number, remaining: number) => ({
    item: item('cancelled'),
    refundedAmountCents: refunded,
    remainingBalanceCents: remaining,
  });
  let open: () => void = () => undefined;
  const holdWallet = () => {
    walletGate = new Promise<void>((resolve) => (open = resolve));
  };
  afterEach(() => open());

  it('writes the remaining balance the API returned, not the refunded amount, and does not wait for the wallet read', async () => {
    detail = { status: 200, body: booking(item('queued', { queuePosition: 1 })) };
    commandReply = () => ({ status: 200, body: cancelBody(1890, 5000) });
    await openDetail();
    await screen.findByText('Na fila');
    await waitFor(() => expect(queryClient.getQueryData(['wallet'])).toMatchObject({ balanceCents: 3110 }));
    holdWallet();
    detail = { status: 200, body: booking(item('cancelled')) };

    await fireEvent.press(screen.getByRole('button', { name: 'Cancelar wash' }));
    await fireEvent.press(screen.getByRole('button', { name: 'Confirmar cancelamento de wash' }));

    // The wallet read is held: the result is already shown and the cache holds exactly the API's balance.
    expect(await screen.findByText(/Estorno confirmado/)).toBeOnTheScreen();
    expect(queryClient.getQueryData(['wallet'])).toMatchObject({ balanceCents: 5000 });
  });

  it('leaves the cached balance untouched when the cancellation fails', async () => {
    detail = { status: 200, body: booking(item('queued', { queuePosition: 1 })) };
    commandReply = () => ({ status: 409, body: { errorCode: 'INVALID_STATE_TRANSITION' } });
    await openDetail();
    await screen.findByText('Na fila');
    await waitFor(() => expect(queryClient.getQueryData(['wallet'])).toMatchObject({ balanceCents: 3110 }));
    holdWallet();

    await fireEvent.press(screen.getByRole('button', { name: 'Cancelar wash' }));
    await fireEvent.press(screen.getByRole('button', { name: 'Confirmar cancelamento de wash' }));

    expect(await screen.findByText(/O estado da execução mudou/)).toBeOnTheScreen();
    expect(queryClient.getQueryData(['wallet'])).toMatchObject({ balanceCents: 3110 });
  });

  it('also leaves the balance untouched when a success response is not valid', async () => {
    detail = { status: 200, body: booking(item('queued', { queuePosition: 1 })) };
    commandReply = () => ({ status: 200, body: { item: item('cancelled'), remainingBalanceCents: 9999 } });
    await openDetail();
    await screen.findByText('Na fila');
    await waitFor(() => expect(queryClient.getQueryData(['wallet'])).toMatchObject({ balanceCents: 3110 }));
    holdWallet();

    await fireEvent.press(screen.getByRole('button', { name: 'Cancelar wash' }));
    await fireEvent.press(screen.getByRole('button', { name: 'Confirmar cancelamento de wash' }));

    expect(await screen.findByText(/Não foi possível concluir a ação/)).toBeOnTheScreen();
    expect(queryClient.getQueryData(['wallet'])).toMatchObject({ balanceCents: 3110 });
  });

  it('keeps a confirmed cancellation and its refund even when the wallet cache cannot be updated', async () => {
    detail = { status: 200, body: booking(item('queued', { queuePosition: 1 })) };
    commandReply = () => ({ status: 200, body: cancelBody(1890, 5000) });
    await openDetail();
    await screen.findByText('Na fila');
    const failing = jest.spyOn(queryClient, 'cancelQueries').mockRejectedValue(new Error('cache exploded'));

    await fireEvent.press(screen.getByRole('button', { name: 'Cancelar wash' }));
    await fireEvent.press(screen.getByRole('button', { name: 'Confirmar cancelamento de wash' }));

    expect(await screen.findByText(/Estorno confirmado/)).toBeOnTheScreen();
    expect(screen.queryByText(/Não foi possível concluir a ação/)).toBeNull();
    expect(posts).toHaveLength(1);
    failing.mockRestore();
  });

  it('re-reads the detail when the offer deadline reaches zero, before any polling tick', async () => {
    const T0 = Date.parse('2026-10-21T12:00:00Z');
    jest.spyOn(Date, 'now').mockImplementation(() => T0);
    detail = {
      status: 200,
      body: booking(item('to_be_confirmed', { machineId: MACHINE_ID, expiresAt: new Date(T0 + 3_000).toISOString() })),
    };
    await openDetail();
    await screen.findByText('Tempo restante: 3 s');
    const before = gets;

    await act(async () => {
      jest.setSystemTime(Date.now() + 3_100);
      await jest.advanceTimersByTimeAsync(3_100);
    });

    expect(await screen.findByText('Oferta vencida')).toBeOnTheScreen();
    expect(gets).toBeGreaterThan(before);
    jest.restoreAllMocks();
  });
});

describe('demo-only Concluir (simulação)', () => {
  const simulationUrl = `http://api.test:5001/api/v1/simulation/machines/${MACHINE_ID}/complete`;
  const running = () => item('in_progress', { machineId: MACHINE_ID });
  const completeBody = {
    machine: { machineId: MACHINE_ID, name: 'washer-01', machineType: 'washer', state: 'available', activeItemId: null, isDemoOccupied: false },
    item: item('completed'),
  };

  beforeEach(() => {
    process.env.EXPO_PUBLIC_DEMO_SIMULATION = 'true';
    detail = { status: 200, body: booking(running()) };
    commandReply = () => ({ status: 200, body: completeBody });
  });
  afterEach(() => {
    delete process.env.EXPO_PUBLIC_DEMO_SIMULATION;
    capabilities.simulation.complete = true;
  });

  it('is hidden unless the demo opt-in variable is set', async () => {
    delete process.env.EXPO_PUBLIC_DEMO_SIMULATION;
    await openDetail();
    await screen.findByText('Em execução');

    expect(screen.queryByRole('button', { name: /\(simulação\)/ })).toBeNull();
  });

  it('is hidden while the capability is not proven', async () => {
    capabilities.simulation.complete = false;
    await openDetail();
    await screen.findByText('Em execução');

    expect(screen.queryByRole('button', { name: /\(simulação\)/ })).toBeNull();
  });

  it.each([
    ['pending_activation', null],
    ['queued', null],
    ['completed', null],
    ['in_progress', null],
  ])('is hidden for an item that is %s without a machine', async (status, machineId) => {
    detail = { status: 200, body: booking(item(status, { machineId })) };
    await openDetail();
    await screen.findByRole('header', { name: 'Contratação 00000000' });

    expect(screen.queryByRole('button', { name: /\(simulação\)/ })).toBeNull();
  });

  it('completes the running machine, labels it as simulation, and shows what the server says afterwards', async () => {
    await openDetail();
    await screen.findByText('Em execução');
    expect(screen.getByText(/Ação de simulação: só existe na demo/)).toBeOnTheScreen();
    detail = { status: 200, body: booking(item('completed')) };

    await fireEvent.press(screen.getByRole('button', { name: 'Concluir wash (simulação)' }));

    expect(await screen.findByText('Execução concluída na simulação.')).toBeOnTheScreen();
    expect(posts).toHaveLength(1);
    expect(posts[0].url).toBe(simulationUrl);
    await waitFor(() => expect(screen.getByText('Concluído')).toBeOnTheScreen());
    expect(screen.queryByRole('button', { name: /\(simulação\)/ })).toBeNull();
    expect(screen.queryByText(/estorno/i)).toBeNull();
    expect(queryClient.getQueryData(['wallet'])).toMatchObject({ balanceCents: 3110 });
  });

  it('explains a 409 (machine not running) and shows the real state', async () => {
    commandReply = () => ({ status: 409, body: { errorCode: 'INVALID_STATE_TRANSITION' } });
    await openDetail();
    await screen.findByText('Em execução');
    detail = { status: 200, body: booking(item('completed')) };

    await fireEvent.press(screen.getByRole('button', { name: 'Concluir wash (simulação)' }));

    expect(await screen.findByText(/O estado da execução mudou/)).toBeOnTheScreen();
    await waitFor(() => expect(screen.getByText('Concluído')).toBeOnTheScreen());
  });

  it('says the simulation is unavailable when the API has no such route (404 without a problem body)', async () => {
    commandReply = () => ({ status: 404, body: null });
    await openDetail();
    await screen.findByText('Em execução');

    await fireEvent.press(screen.getByRole('button', { name: 'Concluir wash (simulação)' }));

    expect(await screen.findByText(/A simulação não está disponível nesta API/)).toBeOnTheScreen();
    expect(screen.getByRole('button', { name: 'Concluir wash (simulação)' })).toBeEnabled();
  });

  it('sends a double tap only once', async () => {
    let finish: (reply: Reply) => void = () => undefined;
    commandReply = () => new Promise<Reply>((resolve) => (finish = resolve));
    await openDetail();
    await screen.findByText('Em execução');

    await fireEvent.press(screen.getByRole('button', { name: 'Concluir wash (simulação)' }));
    await fireEvent.press(screen.getByRole('button', { name: 'Concluir wash (simulação)' }));

    expect(posts).toHaveLength(1);
    finish({ status: 200, body: completeBody });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Concluir wash (simulação)' })).toBeEnabled());
  });
});
