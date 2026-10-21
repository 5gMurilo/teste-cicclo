import { fireEvent, render, screen } from '@testing-library/react-native';

import type { Service } from '../../queries';
import { ServiceCard } from '../ServiceCard';

const WASH: Service = {
  code: 'wash',
  name: 'Lavagem',
  machineType: 'washer',
  unitPriceCents: 1890,
  currency: 'BRL',
  isActive: true,
  canBeBooked: true,
  unavailabilityReason: null,
};

describe('ServiceCard', () => {
  it('shows name, price and availability, and renders no action without callbacks', async () => {
    await render(<ServiceCard service={WASH} />);

    expect(screen.getByText('Lavagem')).toBeOnTheScreen();
    expect(screen.getByText('R$ 18,90')).toBeOnTheScreen();
    expect(screen.getByText('Disponível')).toBeOnTheScreen();
    expect(screen.getByLabelText('Lavagem, R$ 18,90. Disponível')).toBeOnTheScreen();
    expect(screen.queryByRole('button')).toBeNull();
  });

  it.each([
    ['SERVICE_DISABLED', 'Serviço desativado'],
    ['NO_OPERATIONAL_MACHINE', 'Sem máquina operacional no momento'],
    ['SOMETHING_NEW', 'Indisponível no momento'],
  ])('states the reason in text when unavailable (%s)', async (reason, text) => {
    await render(
      <ServiceCard service={{ ...WASH, canBeBooked: false, unavailabilityReason: reason }} />,
    );

    expect(screen.getByText(text)).toBeOnTheScreen();
    expect(screen.queryByText('Disponível')).toBeNull();
  });

  it('renders the price from the prop', async () => {
    await render(<ServiceCard service={{ ...WASH, unitPriceCents: 2500 }} />);

    expect(screen.getByText('R$ 25,00')).toBeOnTheScreen();
    expect(screen.queryByText('R$ 18,90')).toBeNull();
  });

  it('calls onAdd with the service code and labels the action with service and price', async () => {
    const onAdd = jest.fn();
    await render(<ServiceCard service={WASH} onAdd={onAdd} />);

    await fireEvent.press(screen.getByRole('button', { name: 'Adicionar Lavagem, R$ 18,90' }));

    expect(onAdd).toHaveBeenCalledTimes(1);
    expect(onAdd).toHaveBeenCalledWith('wash');
  });

  it('disables the action and never calls onAdd for an unavailable service', async () => {
    const onAdd = jest.fn();
    await render(
      <ServiceCard
        service={{ ...WASH, canBeBooked: false, unavailabilityReason: 'SERVICE_DISABLED' }}
        onAdd={onAdd}
      />,
    );

    const button = screen.getByRole('button', { name: /^Adicionar Lavagem/ });
    expect(button).toBeDisabled();
    await fireEvent.press(button);
    expect(onAdd).not.toHaveBeenCalled();
  });

  it('shows the quantity and a remove action only when units are selected', async () => {
    const onRemove = jest.fn();
    const view = await render(<ServiceCard service={WASH} onAdd={jest.fn()} onRemove={onRemove} />);
    expect(screen.queryByRole('button', { name: /^Remover/ })).toBeNull();

    await view.rerender(<ServiceCard service={WASH} quantity={2} onAdd={jest.fn()} onRemove={onRemove} />);
    expect(screen.getByLabelText('Quantidade: 2')).toBeOnTheScreen();
    await fireEvent.press(screen.getByRole('button', { name: 'Remover Lavagem, R$ 18,90' }));
    expect(onRemove).toHaveBeenCalledWith('wash');
  });
});
