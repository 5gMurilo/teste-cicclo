import { fireEvent, renderRouter, screen, waitFor } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

const APP_DIR = './src/app';

const WALLET = { balanceCents: 5000, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' };
const WASH = {
  code: 'wash',
  name: 'Lavagem',
  machineType: 'washer',
  unitPriceCents: 1890,
  currency: 'BRL',
  isActive: true,
  canBeBooked: true,
  unavailabilityReason: null,
};
const DRY = { ...WASH, code: 'dry', name: 'Secagem', machineType: 'dryer', unitPriceCents: 2090 };

type Reply = { status: number; body: unknown };
const calls: { url: string; method: string }[] = [];

function reply({ status, body }: Reply): Promise<Response> {
  return Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);
}

let gate: Promise<void> = Promise.resolve();

function serve(services: (call: number) => Reply) {
  let servicesCalls = 0;
  globalThis.fetch = jest.fn(async (url: string | URL | Request, init?: RequestInit) => {
    calls.push({ url: String(url), method: init?.method ?? 'GET' });
    if (String(url).endsWith('/wallet')) return reply({ status: 200, body: WALLET });
    await gate;
    return reply(services(++servicesCalls));
  }) as unknown as typeof fetch;
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  calls.length = 0;
  gate = Promise.resolve();
});

afterEach(() => queryClient.clear());

describe('Home reading states', () => {
  it('shows loading first, then the services with API prices, and never posts', async () => {
    let open: () => void = () => undefined;
    gate = new Promise((resolve) => (open = resolve));
    serve(() => ({ status: 200, body: { services: [WASH, { ...DRY, unitPriceCents: 2500 }] } }));

    await renderRouter(APP_DIR);

    expect(screen.getByLabelText('Carregando serviços')).toBeOnTheScreen();
    open();
    expect(await screen.findByText('Lavagem')).toBeOnTheScreen();
    expect(screen.getByText('R$ 18,90')).toBeOnTheScreen();
    expect(screen.getByText('Secagem')).toBeOnTheScreen();
    expect(screen.getByText('R$ 25,00')).toBeOnTheScreen();
    expect(calls.length).toBeGreaterThan(0);
    expect(calls.every((call) => call.method === 'GET')).toBe(true);
  });

  it('shows an empty state for an empty catalog', async () => {
    serve(() => ({ status: 200, body: { services: [] } }));

    await renderRouter(APP_DIR);

    expect(await screen.findByText('Nenhum serviço disponível no momento.')).toBeOnTheScreen();
  });

  it('shows an error with retry and recovers on the next attempt', async () => {
    serve((n) => (n === 1 ? { status: 404, body: { errorCode: 'NOT_FOUND' } } : { status: 200, body: { services: [WASH] } }));

    await renderRouter(APP_DIR);

    expect(await screen.findByText('Não foi possível carregar os serviços.')).toBeOnTheScreen();
    await fireEvent.press(screen.getByRole('button', { name: 'Recarregar serviços' }));
    expect(await screen.findByText('Lavagem')).toBeOnTheScreen();
    expect(screen.queryByText('Não foi possível carregar os serviços.')).toBeNull();
  });

  it('shows an unavailable service with its reason', async () => {
    const disabled = { ...WASH, isActive: false, canBeBooked: false, unavailabilityReason: 'SERVICE_DISABLED' };
    serve(() => ({ status: 200, body: { services: [disabled, DRY] } }));

    await renderRouter(APP_DIR);

    expect(await screen.findByText('Serviço desativado')).toBeOnTheScreen();
    expect(screen.getByText('Disponível')).toBeOnTheScreen();
  });

  it('keeps showing the last catalog with a warning when a refresh fails', async () => {
    serve((n) => (n === 1 ? { status: 200, body: { services: [WASH] } } : { status: 404, body: {} }));

    await renderRouter(APP_DIR);
    expect(await screen.findByText('Lavagem')).toBeOnTheScreen();

    await queryClient.invalidateQueries({ queryKey: ['services'] });

    await waitFor(() =>
      expect(screen.getByText('Preços e disponibilidade podem estar desatualizados.')).toBeOnTheScreen(),
    );
    expect(screen.getByText('Lavagem')).toBeOnTheScreen();
  });
});
