import { fireEvent } from '@testing-library/react-native';
import { renderRouter, screen } from 'expo-router/testing-library';

import { queryClient } from '../src/shared/api/queryClient';

const APP_DIR = './src/app';

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
});

describe('tab navigation', () => {
  it('opens on Home without a deep link and orders the tabs Serviços, Home, Checkout', async () => {
    const app = await renderRouter(APP_DIR);

    expect(app.getPathname()).toBe('/');
    expect(screen.getByLabelText('Serviços, tab, 1 of 3')).toBeOnTheScreen();
    expect(screen.getByLabelText('Home, tab, 2 of 3')).toBeOnTheScreen();
    expect(screen.getByLabelText('Checkout, tab, 3 of 3')).toBeOnTheScreen();
    expect(screen.getByLabelText('Home, tab, 2 of 3')).toBeSelected();
  });

  it('navigates to every tab and shows honest placeholders without ever posting', async () => {
    const methods: string[] = [];
    globalThis.fetch = jest.fn(async (_url: string | URL | Request, init?: RequestInit) => {
      methods.push(init?.method ?? 'GET');
      return {
        status: 404,
        ok: false,
        headers: { get: () => null },
        text: async () => '{}',
      } as unknown as Response;
    }) as unknown as typeof fetch;
    try {
      const app = await renderRouter(APP_DIR);

      await fireEvent.press(screen.getByLabelText('Serviços, tab, 1 of 3'));
      expect(app.getPathname()).toBe('/services');
      expect(await screen.findByText(/A listagem de contratações não está disponível/)).toBeOnTheScreen();

      await fireEvent.press(screen.getByLabelText('Checkout, tab, 3 of 3'));
      expect(app.getPathname()).toBe('/checkout');
      expect(screen.getByText('Nenhum serviço selecionado.')).toBeOnTheScreen();

      await fireEvent.press(screen.getByLabelText('Home, tab, 2 of 3'));
      expect(app.getPathname()).toBe('/');
      expect(methods.every((method) => method === 'GET')).toBe(true);
    } finally {
      queryClient.clear();
    }
  });
});
