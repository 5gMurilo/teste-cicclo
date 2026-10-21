import { fireEvent, renderRouter, screen, waitFor } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

const APP_DIR = './src/app';
const HINT = /Defina EXPO_PUBLIC_API_URL em apps\/mobile\/\.env e reinicie o Metro/;
const BOOKING_ID = '3f8e2c1a-9b7d-4c55-8a1e-0d2f6b7a9c10';

// Regression: with EXPO_PUBLIC_API_URL unset (no apps/mobile/.env when Metro started) every read
// failed with the same generic text, which sent the user hunting for a network problem.
beforeEach(() => {
  delete process.env.EXPO_PUBLIC_API_URL;
  globalThis.fetch = jest.fn() as unknown as typeof fetch;
});

afterEach(() => queryClient.clear());

describe('missing API URL', () => {
  it('tells the user which variable to set in the header, Home and Serviços instead of a generic error', async () => {
    await renderRouter(APP_DIR);

    await waitFor(() => expect(screen.getAllByText(HINT)).toHaveLength(2));
    expect(screen.queryByText('Saldo indisponível')).toBeNull();
    expect(screen.queryByText('Não foi possível carregar os serviços.')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Tentar novamente' })).toBeNull();

    await fireEvent.press(screen.getByLabelText('Serviços, tab, 1 of 3'));
    await waitFor(() => expect(screen.getAllByText(HINT)).toHaveLength(2));
    expect(screen.queryByText('Não foi possível carregar suas contratações.')).toBeNull();
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it('does the same on the booking detail', async () => {
    await renderRouter(APP_DIR, { initialUrl: `/bookings/${BOOKING_ID}` });

    expect(await screen.findByText(HINT)).toBeOnTheScreen();
    expect(screen.queryByText('Não foi possível carregar a contratação.')).toBeNull();
  });
});
