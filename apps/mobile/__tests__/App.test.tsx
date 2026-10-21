import { render, screen } from '@testing-library/react-native';

import App from '../App';

test('renders Cicclo centered without requesting the API', async () => {
  const request = jest.spyOn(global, 'fetch');
  try {
    await render(<App />);
    expect(screen.getByRole('header', { name: 'Cicclo' })).toBeOnTheScreen();
    expect(screen.getByTestId('home')).toHaveStyle({
      flex: 1,
      alignItems: 'center',
      justifyContent: 'center',
    });
    expect(screen.getAllByText('Cicclo')).toHaveLength(1);
    expect(request).not.toHaveBeenCalled();
  } finally {
    request.mockRestore();
  }
});
