import { fireEvent, renderRouter, screen, within } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

const APP_DIR = './src/app';

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

function respond(body: unknown): Promise<Response> {
  return Promise.resolve({
    status: 200,
    ok: true,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  globalThis.fetch = jest.fn((url: string | URL | Request) =>
    respond(
      String(url).endsWith('/wallet')
        ? { balanceCents: 5000, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' }
        : { services: [service('wash', 'Lavagem', 1890), service('dry', 'Secagem', 2090)] },
    ),
  ) as unknown as typeof fetch;
});

afterEach(() => queryClient.clear());

const add = (name: string) => screen.getByRole('button', { name: new RegExp(`^Adicionar ${name}`) });

describe('Checkout tab badge', () => {
  it('hides the badge at zero and sums every occurrence, naming the quantity for accessibility', async () => {
    await renderRouter(APP_DIR);
    await screen.findByText('Lavagem');
    await screen.findByText('R$ 50,00');
    expect(within(screen.getByLabelText('Checkout, tab, 3 of 3')).queryByText(/\d/)).toBeNull();

    await fireEvent.press(add('Lavagem'));
    expect(within(screen.getByLabelText('Checkout, 1 item, tab, 3 of 3')).getByText('1')).toBeOnTheScreen();

    await fireEvent.press(add('Lavagem'));
    expect(within(screen.getByLabelText('Checkout, 2 itens, tab, 3 of 3')).getByText('2')).toBeOnTheScreen();
  });

  it('drops the badge when the selection is emptied, and keeps tab order, selection and header across tabs', async () => {
    const app = await renderRouter(APP_DIR);
    await screen.findByText('Lavagem');
    await screen.findByText('R$ 50,00');
    await fireEvent.press(add('Lavagem'));
    await fireEvent.press(add('Secagem'));
    expect(screen.getByLabelText('Checkout, 2 itens, tab, 3 of 3')).toBeOnTheScreen();

    await fireEvent.press(screen.getByLabelText('Serviços, tab, 1 of 3'));
    expect(app.getPathname()).toBe('/services');
    await fireEvent.press(screen.getByLabelText(/^Checkout.*tab, 3 of 3$/));
    expect(screen.getByLabelText('Subtotal da seleção: R$ 39,80')).toBeOnTheScreen();
    expect(screen.getByLabelText('Saldo da carteira: R$ 50,00')).toBeOnTheScreen();

    await fireEvent.press(screen.getByRole('button', { name: 'Remover Lavagem' }));
    await fireEvent.press(screen.getByRole('button', { name: 'Remover Secagem' }));
    expect(screen.getByLabelText('Checkout, tab, 3 of 3')).toBeOnTheScreen();
    expect(screen.getByLabelText('Home, tab, 2 of 3')).toBeOnTheScreen();
  });
});
