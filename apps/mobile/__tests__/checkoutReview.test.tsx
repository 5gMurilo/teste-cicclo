import { fireEvent, renderRouter, screen, waitFor } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

const APP_DIR = './src/app';
const nbsp = (text: string) => text.replace(/R\$ /g, 'R$ ');

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
const requests: { url: string; method: string }[] = [];

function respond({ status, body }: Reply): Promise<Response> {
  return Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);
}

function setServices(...services: ReturnType<typeof service>[]) {
  servicesReply = { status: 200, body: { services } };
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  requests.length = 0;
  globalThis.fetch = jest.fn((url: string | URL | Request, init?: RequestInit) => {
    requests.push({ url: String(url), method: init?.method ?? 'GET' });
    return respond(String(url).endsWith('/wallet') ? walletReply : servicesReply);
  }) as unknown as typeof fetch;
  walletReply = { status: 200, body: { balanceCents: 5000, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' } };
  setServices(service('wash', 'Lavagem', 1890), service('dry', 'Secagem', 2090));
});

afterEach(() => queryClient.clear());

async function openCheckoutWith(...names: string[]) {
  const app = await renderRouter(APP_DIR);
  await screen.findByText('Lavagem');
  await screen.findByText(nbsp('R$ 50,00'));
  for (const name of names) {
    await fireEvent.press(screen.getByRole('button', { name: new RegExp(`^Adicionar ${name}`) }));
  }
  await fireEvent.press(screen.getByLabelText('Checkout, tab, 3 of 3'));
  return app;
}

describe('Checkout review', () => {
  it('offers Escolher serviços when empty and returns to Home without any POST', async () => {
    const app = await openCheckoutWith();

    expect(screen.getByText('Nenhum serviço selecionado.')).toBeOnTheScreen();
    await fireEvent.press(screen.getByRole('button', { name: 'Escolher serviços' }));

    expect(app.getPathname()).toBe('/');
    expect(requests.every((r) => r.method === 'GET')).toBe(true);
  });

  it('groups repeated services into one line with quantity and unit price', async () => {
    await openCheckoutWith('Lavagem', 'Lavagem');

    expect(screen.getByLabelText(`Lavagem, 2 unidades, ${nbsp('R$ 18,90')} cada`)).toBeOnTheScreen();
    expect(screen.getByLabelText(`Subtotal da seleção: ${nbsp('R$ 37,80')}`)).toBeOnTheScreen();
    expect(screen.getByLabelText(`Saldo estimado após contratação: ${nbsp('R$ 12,20')}`)).toBeOnTheScreen();
  });

  it('shows the three labels for a valid selection: 3980, 5000 and 1020', async () => {
    await openCheckoutWith('Lavagem', 'Secagem');

    expect(screen.getByLabelText(`Subtotal da seleção: ${nbsp('R$ 39,80')}`)).toBeOnTheScreen();
    expect(screen.getByLabelText(`Saldo confirmado: ${nbsp('R$ 50,00')}`)).toBeOnTheScreen();
    expect(screen.getByLabelText(`Saldo estimado após contratação: ${nbsp('R$ 10,20')}`)).toBeOnTheScreen();
  });

  it('defaults to no preference, accepts a selected service and clears it when its last unit is removed', async () => {
    await openCheckoutWith('Lavagem', 'Secagem');
    expect(screen.getByRole('radio', { name: 'Sem preferência' })).toBeSelected();

    await fireEvent.press(screen.getByRole('radio', { name: 'Secagem' }));
    expect(screen.getByRole('radio', { name: 'Secagem' })).toBeSelected();

    await fireEvent.press(screen.getByRole('button', { name: 'Remover Secagem' }));
    expect(screen.queryByRole('radio', { name: 'Secagem' })).toBeNull();
    expect(screen.getByRole('radio', { name: 'Sem preferência' })).toBeSelected();
  });

  it('requires a new review when a price changes and clears the alert only on acceptance', async () => {
    await openCheckoutWith('Lavagem');
    setServices(service('wash', 'Lavagem', 2500), service('dry', 'Secagem', 2090));

    await queryClient.invalidateQueries({ queryKey: ['services'] });

    await waitFor(() =>
      expect(
        screen.getByText(`O preço de Lavagem mudou de ${nbsp('R$ 18,90')} para ${nbsp('R$ 25,00')}.`),
      ).toBeOnTheScreen(),
    );
    expect(screen.getByLabelText(`Subtotal da seleção: ${nbsp('R$ 25,00')}`)).toBeOnTheScreen();

    await fireEvent.press(screen.getByRole('button', { name: 'Aceitar novos preços' }));
    expect(screen.queryByText(/mudou de/)).toBeNull();
  });

  it('asks to remove a service that became unavailable after it was selected', async () => {
    await openCheckoutWith('Lavagem');
    setServices(
      service('wash', 'Lavagem', 1890, { isActive: false, canBeBooked: false, unavailabilityReason: 'SERVICE_DISABLED' }),
      service('dry', 'Secagem', 2090),
    );

    await queryClient.invalidateQueries({ queryKey: ['services'] });

    await waitFor(() => expect(screen.getByText('Remova os serviços indisponíveis: wash.')).toBeOnTheScreen());
  });

  it('warns about immediate charge and simulated machines, and exposes no purchase action', async () => {
    await openCheckoutWith('Lavagem');

    expect(screen.getByText(/cobrado imediatamente, mesmo que o serviço entre em fila/)).toBeOnTheScreen();
    expect(screen.getByText(/máquinas são simuladas/)).toBeOnTheScreen();
    expect(screen.getByText('A contratação ainda não está habilitada nesta versão.')).toBeOnTheScreen();
    expect(screen.queryByRole('button', { name: /contratar|confirmar|comprar/i })).toBeNull();
    expect(requests.every((r) => r.method === 'GET')).toBe(true);
  });
});
