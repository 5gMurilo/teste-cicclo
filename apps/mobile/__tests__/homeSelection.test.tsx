import { fireEvent, renderRouter, screen } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

const APP_DIR = './src/app';
const nbsp = (text: string) => text.replace('R$ ', 'R$ ');

const service = (code: string, name: string, unitPriceCents: number, over: Record<string, unknown> = {}) => ({
  code,
  name,
  machineType: code === 'dry' ? 'dryer' : 'washer',
  unitPriceCents,
  currency: 'BRL',
  isActive: true,
  canBeBooked: true,
  unavailabilityReason: null,
  ...over,
});

type Reply = { status: number; body: unknown };
let walletReply: Reply;
let servicesReply: Reply;
const methods: string[] = [];

function respond({ status, body }: Reply): Promise<Response> {
  return Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);
}

function setApi(balanceCents: number, ...services: ReturnType<typeof service>[]) {
  walletReply = { status: 200, body: { balanceCents, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' } };
  servicesReply = { status: 200, body: { services } };
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  methods.length = 0;
  globalThis.fetch = jest.fn((url: string | URL | Request, init?: RequestInit) => {
    methods.push(init?.method ?? 'GET');
    return respond(String(url).endsWith('/wallet') ? walletReply : servicesReply);
  }) as unknown as typeof fetch;
  setApi(5000, service('wash', 'Lavagem', 1890), service('dry', 'Secagem', 2090));
});

afterEach(() => queryClient.clear());

const add = (name: string) => screen.getByRole('button', { name: new RegExp(`^Adicionar ${name}`) });

describe('Home selection', () => {
  it('selecting wash and dry shows 3980 selected and 1020 available while the header stays at 5000', async () => {
    await renderRouter(APP_DIR);
    await screen.findByText('Lavagem');
    expect(await screen.findByText(nbsp('R$ 50,00'))).toBeOnTheScreen();

    await fireEvent.press(add('Lavagem'));
    await fireEvent.press(add('Secagem'));

    expect(screen.getByText(`Selecionado: ${nbsp('R$ 39,80')}`)).toBeOnTheScreen();
    expect(screen.getByText(`Disponível para adicionar: ${nbsp('R$ 10,20')}`)).toBeOnTheScreen();
    expect(screen.getByLabelText(`Saldo: ${nbsp('R$ 50,00')}`)).toBeOnTheScreen();
    expect(methods.every((method) => method === 'GET')).toBe(true);
  });

  it('keeps a repeated service as quantity 2 and removes one unit at a time', async () => {
    await renderRouter(APP_DIR);
    await screen.findByText('Lavagem');
    await screen.findByText(nbsp('R$ 50,00'));

    await fireEvent.press(add('Lavagem'));
    await fireEvent.press(add('Lavagem'));
    expect(screen.getByLabelText('Quantidade: 2')).toBeOnTheScreen();
    expect(screen.getByText(`Selecionado: ${nbsp('R$ 37,80')}`)).toBeOnTheScreen();

    await fireEvent.press(screen.getByRole('button', { name: /^Remover Lavagem/ }));
    expect(screen.getByLabelText('Quantidade: 1')).toBeOnTheScreen();
    await fireEvent.press(screen.getByRole('button', { name: /^Remover Lavagem/ }));
    expect(screen.queryByLabelText(/^Quantidade/)).toBeNull();
    expect(screen.getByText(`Selecionado: ${nbsp('R$ 0,00')}`)).toBeOnTheScreen();
  });

  it('explains an insufficient balance in an alert and keeps the previous selection', async () => {
    await renderRouter(APP_DIR);
    await screen.findByText('Lavagem');
    await screen.findByText(nbsp('R$ 50,00'));
    await fireEvent.press(add('Lavagem'));
    await fireEvent.press(add('Secagem'));

    await fireEvent.press(add('Lavagem'));

    expect(screen.getByRole('alert')).toHaveTextContent('Saldo insuficiente para adicionar este serviço.');
    expect(screen.getByText(`Selecionado: ${nbsp('R$ 39,80')}`)).toBeOnTheScreen();
    expect(screen.getAllByLabelText('Quantidade: 1')).toHaveLength(2);
  });

  it('blocks additions with an explanation when the wallet is invalid', async () => {
    walletReply = { status: 200, body: { balanceCents: -1, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' } };
    await renderRouter(APP_DIR);
    await screen.findByText('Lavagem');
    await screen.findByText('Saldo indisponível');

    await fireEvent.press(add('Lavagem'));

    expect(screen.getByRole('alert')).toHaveTextContent(/^Aguarde o saldo e o catálogo serem confirmados/);
    expect(screen.queryByText(/^Selecionado/)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Revisar seleção' })).toBeNull();
  });

  it('does not offer selection for an unavailable service', async () => {
    setApi(
      5000,
      service('wash', 'Lavagem', 1890, { isActive: false, canBeBooked: false, unavailabilityReason: 'SERVICE_DISABLED' }),
      service('dry', 'Secagem', 2090),
    );
    await renderRouter(APP_DIR);
    await screen.findByText('Lavagem');
    await screen.findByText(nbsp('R$ 50,00'));

    expect(add('Lavagem')).toBeDisabled();
    expect(screen.getByText('Serviço desativado')).toBeOnTheScreen();
    await fireEvent.press(add('Lavagem'));
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('offers the review CTA only with a selection and opens the Checkout tab', async () => {
    const app = await renderRouter(APP_DIR);
    await screen.findByText('Lavagem');
    await screen.findByText(nbsp('R$ 50,00'));
    expect(screen.queryByRole('button', { name: 'Revisar seleção' })).toBeNull();

    await fireEvent.press(add('Lavagem'));
    await fireEvent.press(screen.getByRole('button', { name: 'Revisar seleção' }));

    expect(app.getPathname()).toBe('/checkout');
  });
});
