import { act } from '@testing-library/react-native';
import { fireEvent, renderRouter, screen, waitFor } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

const APP_DIR = './src/app';

function reply(status: number, body: unknown): Promise<Response> {
  return Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);
}

const WALLET = { balanceCents: 5000, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' };

// The header only cares about /wallet; any other request (the Home catalog) gets an empty list.
function serveWallet(wallet: (call: number) => Promise<Response>) {
  let walletCalls = 0;
  globalThis.fetch = jest.fn((url: string | URL | Request) =>
    String(url).endsWith('/wallet') ? wallet(++walletCalls) : reply(200, { services: [] }),
  ) as unknown as typeof fetch;
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  delete process.env.EXPO_PUBLIC_DEMO_DISPLAY_NAME;
});

afterEach(() => queryClient.clear());

describe('shared header in the tab layout', () => {
  it('greets Visitante and shows the API balance 5000 as R$ 50,00 on every tab', async () => {
    serveWallet(() => reply(200, WALLET));

    await renderRouter(APP_DIR);

    expect(screen.getByText('Carregando saldo…')).toBeOnTheScreen();
    expect(await screen.findByText('R$ 50,00')).toBeOnTheScreen();
    expect(screen.getByRole('header', { name: 'Olá! Visitante' })).toBeOnTheScreen();

    await fireEvent.press(screen.getByLabelText('Checkout, tab, 3 of 3'));
    expect(screen.getByText('R$ 50,00')).toBeOnTheScreen();
  });

  it('shows an error without a balance and recovers through the retry action', async () => {
    serveWallet((n) => (n === 1 ? reply(404, { errorCode: 'NOT_FOUND' }) : reply(200, WALLET)));

    await renderRouter(APP_DIR);

    expect(await screen.findByText('Saldo indisponível')).toBeOnTheScreen();
    expect(screen.queryByText(/R\$/)).toBeNull();

    await fireEvent.press(screen.getByRole('button', { name: 'Tentar novamente' }));
    expect(await screen.findByText('R$ 50,00')).toBeOnTheScreen();
  });

  it('keeps the last confirmed balance but labels it outdated when a refresh fails', async () => {
    serveWallet((n) => (n === 1 ? reply(200, WALLET) : reply(404, { errorCode: 'NOT_FOUND' })));

    await renderRouter(APP_DIR);
    expect(await screen.findByText('R$ 50,00')).toBeOnTheScreen();

    await act(async () => {
      await queryClient.invalidateQueries({ queryKey: ['wallet'] });
    });

    await waitFor(() => expect(screen.getByText(/^Desatualizado · atualizado às /)).toBeOnTheScreen());
    expect(screen.getByText('R$ 50,00')).toBeOnTheScreen();
  });
});
