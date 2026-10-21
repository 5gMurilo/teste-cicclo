import { fireEvent, renderRouter, screen, waitFor } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

// Purchases are gated by a capability proven only against the real API; these tests exercise the enabled path.
jest.mock('../src/shared/api/capabilities', () => ({ capabilities: { purchase: true, keyRetentionMs: null, itemCommands: { activate: false, confirm: false, cancel: false, reportIssue: false }, simulation: { complete: false } } }));
jest.mock('expo-crypto', () => {
  let n = 0;
  return { randomUUID: () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}` };
});

const APP_DIR = './src/app';
const nbsp = (text: string) => text.replace(/R\$ /g, 'R$ ');
const CHECKOUT_TAB = /^Checkout.*tab, 3 of 3$/;

const service = (code: string, name: string, unitPriceCents: number) => ({
  code,
  name,
  machineType: code === 'dry' ? 'dryer' : 'washer',
  unitPriceCents,
  currency: 'BRL',
  isActive: true,
  canBeBooked: true,
  unavailabilityReason: null,
});
const bookingFor = (chargedAmountCents: number, remainingBalanceCents: number) => ({
  bookingId: '11111111-1111-4111-8111-111111111111',
  currency: 'BRL',
  chargedAmountCents,
  remainingBalanceCents,
  createdAt: '2026-10-21T12:00:00Z',
  items: [
    {
      itemId: '22222222-2222-4222-8222-222222222222',
      serviceCode: 'wash',
      unitPriceCents: chargedAmountCents,
      status: 'queued',
      machineId: null,
      queuePosition: 1,
      expiresAt: null,
    },
  ],
});

type Post = { key: string | null; body: string };
let posts: Post[];
let postAnswers: (() => Promise<Response>)[];
let balance: number;
let washPrice: number;
let washDisabled: boolean;
let clock: number;

function response(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  const lower = Object.fromEntries(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), v]));
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: { get: (name: string) => lower[name.toLowerCase()] ?? null },
    text: async () => JSON.stringify(body),
  } as unknown as Response;
}
const reply = (status: number, body: unknown, headers?: Record<string, string>) => () =>
  Promise.resolve(response(status, body, headers));
const networkFailure = () => Promise.reject(new TypeError('Network request failed'));

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  posts = [];
  postAnswers = [];
  balance = 5000;
  washPrice = 1890;
  washDisabled = false;
  clock = Date.parse('2026-10-21T12:00:00Z');
  jest.spyOn(Date, 'now').mockImplementation(() => clock);
  globalThis.fetch = jest.fn((url: string | URL | Request, init?: RequestInit) => {
    if (init?.method === 'POST') {
      posts.push({ key: new Headers(init.headers).get('idempotency-key'), body: String(init.body) });
      return (postAnswers.shift() ?? networkFailure)();
    }
    return Promise.resolve(
      String(url).endsWith('/wallet')
        ? response(200, { balanceCents: balance, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' })
        : response(200, { services: [{ ...service('wash', 'Lavagem', washPrice), ...(washDisabled ? { isActive: false, canBeBooked: false, unavailabilityReason: 'SERVICE_DISABLED' } : {}) }, service('dry', 'Secagem', 2090)] }),
    );
  }) as unknown as typeof fetch;
});

afterEach(() => {
  jest.restoreAllMocks();
  queryClient.clear();
});

async function startReview(...names: string[]) {
  const app = await renderRouter(APP_DIR);
  await screen.findByText('Lavagem');
  await screen.findByText(nbsp('R$ 50,00'));
  for (const name of names) {
    await fireEvent.press(screen.getByRole('button', { name: new RegExp(`^Adicionar ${name}`) }));
  }
  await fireEvent.press(screen.getByLabelText(CHECKOUT_TAB));
  return app;
}

const confirm = () => fireEvent.press(screen.getByRole('button', { name: /^Confirmar contratação/ }));

describe('purchase from the review screen', () => {
  it('confirms a wash: exact request, balance 5000 to 3110 in the header, selection cleared', async () => {
    postAnswers = [
      async () => {
        balance = 3110;
        return response(201, bookingFor(1890, 3110));
      },
    ];
    await startReview('Lavagem');

    await confirm();

    expect(await screen.findByText('Contratação confirmada')).toBeOnTheScreen();
    expect(posts).toHaveLength(1);
    expect(JSON.parse(posts[0].body)).toEqual({ serviceCodes: ['wash'], expectedTotalCents: 1890 });
    expect(posts[0].key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(screen.getByLabelText(`Saldo restante: ${nbsp('R$ 31,10')}`)).toBeOnTheScreen();
    expect(await screen.findByLabelText(`Saldo da carteira: ${nbsp('R$ 31,10')}`)).toBeOnTheScreen();
    expect(screen.getByLabelText('Checkout, tab, 3 of 3')).toBeOnTheScreen();
  });

  it('treats a 200 replay as the same single confirmed booking', async () => {
    postAnswers = [reply(200, bookingFor(1890, 1020))];
    await startReview('Lavagem');

    await confirm();

    expect(await screen.findByText('Contratação confirmada')).toBeOnTheScreen();
    expect(screen.getByText('Código: 11111111-1111-4111-8111-111111111111')).toBeOnTheScreen();
  });

  it('shows progress and offers no second action while the purchase is in flight, even across tabs', async () => {
    let finish: (response: Response) => void = () => undefined;
    postAnswers = [() => new Promise<Response>((resolve) => (finish = resolve))];
    await startReview('Lavagem');

    await confirm();
    expect(screen.getByText('Contratando…')).toBeOnTheScreen();
    expect(screen.queryByRole('button', { name: /^Confirmar contratação/ })).toBeNull();

    await fireEvent.press(screen.getByLabelText('Home, tab, 2 of 3'));
    await fireEvent.press(screen.getByLabelText(CHECKOUT_TAB));
    expect(screen.getByText('Contratando…')).toBeOnTheScreen();
    expect(posts).toHaveLength(1);

    finish(response(201, bookingFor(1890, 3110)));
    expect(await screen.findByText('Contratação confirmada')).toBeOnTheScreen();
  });

  it('explains a definitive refusal, keeps the selection and never shows a charge', async () => {
    postAnswers = [reply(409, { errorCode: 'WALLET_INSUFFICIENT_BALANCE' })];
    await startReview('Lavagem');

    await confirm();

    expect(await screen.findByText(/Saldo insuficiente\. Nenhuma cobrança foi feita/)).toBeOnTheScreen();
    expect(screen.queryByText('Contratação confirmada')).toBeNull();
    await fireEvent.press(screen.getByRole('button', { name: 'Voltar à revisão' }));
    expect(screen.getByLabelText(`Subtotal da seleção: ${nbsp('R$ 18,90')}`)).toBeOnTheScreen();
    expect(screen.getByLabelText('Checkout, 1 item, tab, 3 of 3')).toBeOnTheScreen();
  });

  it('after PRICE_CHANGED requires accepting the new price, then uses a new key and the new total', async () => {
    postAnswers = [reply(409, { errorCode: 'PRICE_CHANGED' }), reply(201, bookingFor(2500, 2500))];
    await startReview('Lavagem');
    washPrice = 2500;

    await confirm();
    expect(await screen.findByText(/O preço mudou\. Nenhuma cobrança foi feita/)).toBeOnTheScreen();
    await fireEvent.press(screen.getByRole('button', { name: 'Voltar à revisão' }));

    await waitFor(() => expect(screen.getByText(/mudou de/)).toBeOnTheScreen());
    expect(screen.getByRole('button', { name: /^Confirmar contratação/ })).toBeDisabled();
    expect(posts).toHaveLength(1);

    await fireEvent.press(screen.getByRole('button', { name: 'Aceitar novos preços' }));
    await confirm();

    expect(await screen.findByText('Contratação confirmada')).toBeOnTheScreen();
    expect(posts).toHaveLength(2);
    expect(JSON.parse(posts[1].body).expectedTotalCents).toBe(2500);
    expect(posts[1].key).not.toBe(posts[0].key);
  });

  it('keeps an unknown outcome recoverable: selection frozen, verify replays the same key and bytes', async () => {
    postAnswers = [networkFailure, reply(200, bookingFor(1890, 3110))];
    await startReview('Lavagem');

    await confirm();
    expect(await screen.findByText(/Não foi possível confirmar se a contratação foi concluída/)).toBeOnTheScreen();

    await fireEvent.press(screen.getByLabelText('Home, tab, 2 of 3'));
    await fireEvent.press(screen.getByRole('button', { name: /^Adicionar Secagem/ }));
    expect(screen.getByRole('alert')).toHaveTextContent(/Há uma contratação em andamento/);

    await fireEvent.press(screen.getByLabelText(/^Checkout.*tab, 3 of 3$/));
    await fireEvent.press(screen.getByRole('button', { name: 'Verificar contratação' }));

    expect(await screen.findByText('Contratação confirmada')).toBeOnTheScreen();
    expect(posts).toHaveLength(2);
    expect(posts[1].key).toBe(posts[0].key);
    expect(posts[1].body).toBe(posts[0].body);
  });

  it('on a rate limit blocks the way back to a new attempt until Retry-After has passed', async () => {
    postAnswers = [reply(429, { errorCode: 'RATE_LIMITED' }, { 'Retry-After': '30' })];
    await startReview('Lavagem');

    await confirm();

    expect(await screen.findByText(/Muitas tentativas\. Nenhuma cobrança foi feita\. Aguarde até/)).toBeOnTheScreen();
    expect(screen.getByRole('button', { name: 'Voltar à revisão' })).toBeDisabled();
    expect(posts).toHaveLength(1);
  });

  it('hides the retry action when the key is in conflict and never offers a new purchase', async () => {
    postAnswers = [reply(409, { errorCode: 'IDEMPOTENCY_CONFLICT' })];
    await startReview('Lavagem');

    await confirm();

    expect(await screen.findByText(/a chave já pertence a outra intenção/)).toBeOnTheScreen();
    expect(screen.queryByRole('button', { name: 'Verificar contratação' })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Confirmar contratação/ })).toBeNull();
  });
});

describe('purchase button guards and request content', () => {
  it('sends the chosen execution preference in the request body', async () => {
    postAnswers = [reply(201, bookingFor(3980, 1020))];
    await startReview('Lavagem', 'Secagem');
    await fireEvent.press(screen.getByRole('radio', { name: 'Secagem' }));

    await confirm();

    await screen.findByText('Contratação confirmada');
    expect(JSON.parse(posts[0].body)).toEqual({
      serviceCodes: ['wash', 'dry'],
      expectedTotalCents: 3980,
      preferredFirstServiceCode: 'dry',
    });
  });

  it('omits the preference from the body when none was chosen', async () => {
    postAnswers = [reply(201, bookingFor(3980, 1020))];
    await startReview('Lavagem', 'Secagem');

    await confirm();

    await screen.findByText('Contratação confirmada');
    expect(JSON.parse(posts[0].body)).not.toHaveProperty('preferredFirstServiceCode');
  });

  it('disables the purchase button when a selected service becomes unavailable', async () => {
    await startReview('Lavagem');
    expect(screen.getByRole('button', { name: /^Confirmar contratação/ })).toBeEnabled();
    washDisabled = true;

    await queryClient.invalidateQueries({ queryKey: ['services'] });

    await waitFor(() => expect(screen.getByText(/Remova os serviços indisponíveis/)).toBeOnTheScreen());
    expect(screen.getByRole('button', { name: /^Confirmar contratação/ })).toBeDisabled();
    expect(posts).toHaveLength(0);
  });

  it('keeps the purchase button disabled when a price rise no longer fits the balance, even after accepting it', async () => {
    await startReview('Lavagem');
    washPrice = 6000;

    await queryClient.invalidateQueries({ queryKey: ['services'] });

    await waitFor(() => expect(screen.getByText(/Saldo insuficiente para esta seleção/)).toBeOnTheScreen());
    expect(screen.getByRole('button', { name: /^Confirmar contratação/ })).toBeDisabled();
    await fireEvent.press(screen.getByRole('button', { name: 'Aceitar novos preços' }));
    expect(screen.getByRole('button', { name: /^Confirmar contratação/ })).toBeDisabled();
    expect(posts).toHaveLength(0);
  });
});
