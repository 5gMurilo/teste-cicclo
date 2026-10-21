import { fireEvent } from '@testing-library/react-native';
import { renderRouter, screen } from 'expo-router/testing-library';

const APP_DIR = './src/app';

describe('tab navigation', () => {
  it('opens on Home without a deep link and orders the tabs Serviços, Home, Checkout', async () => {
    const app = await renderRouter(APP_DIR);

    expect(app.getPathname()).toBe('/');
    expect(screen.getByRole('header', { name: 'Home' })).toBeOnTheScreen();
    expect(screen.getByLabelText('Serviços, tab, 1 of 3')).toBeOnTheScreen();
    expect(screen.getByLabelText('Home, tab, 2 of 3')).toBeOnTheScreen();
    expect(screen.getByLabelText('Checkout, tab, 3 of 3')).toBeOnTheScreen();
    expect(screen.getByLabelText('Home, tab, 2 of 3')).toBeSelected();
  });

  it('navigates to every tab and shows honest placeholders without requesting the API', async () => {
    const request = jest.spyOn(globalThis, 'fetch');
    try {
      const app = await renderRouter(APP_DIR);

      await fireEvent.press(screen.getByLabelText('Serviços, tab, 1 of 3'));
      expect(app.getPathname()).toBe('/services');
      expect(screen.getByText(/aparecerão aqui quando a listagem estiver disponível/)).toBeOnTheScreen();

      await fireEvent.press(screen.getByLabelText('Checkout, tab, 3 of 3'));
      expect(app.getPathname()).toBe('/checkout');
      expect(screen.getByText(/A contratação ainda não está habilitada/)).toBeOnTheScreen();

      await fireEvent.press(screen.getByLabelText('Home, tab, 2 of 3'));
      expect(app.getPathname()).toBe('/');
      expect(request).not.toHaveBeenCalled();
    } finally {
      request.mockRestore();
    }
  });
});
