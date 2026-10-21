import { fireEvent, render, screen } from '@testing-library/react-native';

import { AppHeader, type BalanceState } from '../AppHeader';

async function renderHeader(balance: BalanceState, displayName = 'Ana') {
  const onRetry = jest.fn();
  await render(<AppHeader displayName={displayName} balance={balance} onRetry={onRetry} />);
  return { onRetry };
}

describe('AppHeader', () => {
  it('greets on the left and shows the confirmed balance in BRL with an accessible label', async () => {
    await renderHeader({ status: 'ready', cents: 5000 });

    expect(screen.getByRole('header', { name: 'Olá! Ana' })).toBeOnTheScreen();
    expect(screen.getByText('R$ 50,00')).toBeOnTheScreen();
    expect(screen.getByLabelText('Saldo: R$ 50,00')).toBeOnTheScreen();
  });

  it('shows no currency while loading', async () => {
    await renderHeader({ status: 'loading' });

    expect(screen.getByText('Carregando saldo…')).toBeOnTheScreen();
    expect(screen.queryByText(/R\$/)).toBeNull();
  });

  it('shows an error with a retry action instead of a zero balance', async () => {
    const { onRetry } = await renderHeader({ status: 'error' });

    expect(screen.getByText('Saldo indisponível')).toBeOnTheScreen();
    expect(screen.queryByText(/R\$/)).toBeNull();
    await fireEvent.press(screen.getByRole('button', { name: 'Tentar novamente' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('labels a retained balance as outdated and offers a refresh', async () => {
    const { onRetry } = await renderHeader({
      status: 'stale',
      cents: 1020,
      updatedAtLabel: 'atualizado às 12:00',
    });

    expect(screen.getByText('Desatualizado · atualizado às 12:00')).toBeOnTheScreen();
    expect(screen.getByLabelText('Saldo desatualizado: R$ 10,20, atualizado às 12:00')).toBeOnTheScreen();
    await fireEvent.press(screen.getByRole('button', { name: 'Atualizar saldo' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('wraps a long name instead of truncating the balance and keeps font scaling enabled', async () => {
    const longName = 'Maria Aparecida de Nazaré Albuquerque Figueiredo Santos';
    await renderHeader({ status: 'ready', cents: 5000 }, longName);

    const greeting = screen.getByRole('header', { name: `Olá! ${longName}` });
    expect(greeting.props.numberOfLines).toBe(2);
    expect(greeting.props.allowFontScaling).not.toBe(false);
    expect(screen.getByText('R$ 50,00').props.allowFontScaling).not.toBe(false);
  });
});
