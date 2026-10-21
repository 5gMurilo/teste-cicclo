import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react-native';
import type { ReactNode } from 'react';

import { ApiError } from '../../../shared/api/apiError';
import { InvalidBookingIdError } from '../detailQuery';
import { commandErrorMessage, postItemCommand, SimulationUnavailableError, useItemCommand } from '../itemCommand';

const BOOKING_ID = '00000000-0000-4000-8000-000000000001';
const ITEM_ID = '10000000-0000-4000-8000-000000000001';
const api = (status: number, errorCode: string | null = null, retryAfterAt: number | null = null) =>
  new ApiError({ status, errorCode, traceId: null, retryAfterAt });

const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>
);

describe('useItemCommand', () => {
  it('runs one command at a time: a second call in the same tick sends nothing', async () => {
    let finish: (value: string) => void = () => undefined;
    const send = jest.fn(() => new Promise<string>((resolve) => (finish = resolve)));
    const { result } = await renderHook(() => useItemCommand(send), { wrapper });

    await act(async () => {
      void result.current.run('item-1');
      void result.current.run('item-1');
    });

    expect(send).toHaveBeenCalledTimes(1);
    expect(result.current.state).toMatchObject({ status: 'pending', itemId: 'item-1' });
    await act(async () => finish('ok'));
    expect(result.current.state).toMatchObject({ status: 'done', result: 'ok' });
  });

  it('records a failure and lets the user try again', async () => {
    const send = jest.fn().mockRejectedValueOnce(api(409, 'INVALID_STATE_TRANSITION')).mockResolvedValueOnce('ok');
    const { result } = await renderHook(() => useItemCommand(send), { wrapper });

    await act(async () => result.current.run('item-1'));
    expect(result.current.state).toMatchObject({ status: 'failed' });
    await act(async () => result.current.run('item-1'));

    expect(send).toHaveBeenCalledTimes(2);
    expect(result.current.state).toMatchObject({ status: 'done' });
  });
});

describe('postItemCommand', () => {
  it.each([
    ['a malformed booking id', 'abc', ITEM_ID],
    ['a malformed item id', BOOKING_ID, '../wallet'],
    ['an empty item id', BOOKING_ID, ''],
  ])('sends nothing for %s', async (_name, bookingId, itemId) => {
    const fetchSpy = jest.fn();
    globalThis.fetch = fetchSpy as unknown as typeof fetch;

    await expect(postItemCommand({ bookingId, itemId, action: 'confirm' })).rejects.toBeInstanceOf(InvalidBookingIdError);

    expect(fetchSpy).not.toHaveBeenCalled();
  });
});

describe('commandErrorMessage', () => {
  it('names each failure by status and errorCode only', () => {
    expect(commandErrorMessage(api(404, 'ITEM_NOT_FOUND'))).toBe('Contratação ou execução não encontrada.');
    // A bare 404 on a regular command is never blamed on the demo simulation.
    expect(commandErrorMessage(api(404, null))).toBe('Contratação ou execução não encontrada.');
    expect(commandErrorMessage(new SimulationUnavailableError())).toMatch(/A simulação não está disponível/);
    expect(commandErrorMessage(api(410, 'MACHINE_OFFER_EXPIRED'))).toMatch(/A oferta venceu/);
    expect(commandErrorMessage(api(409, 'INVALID_STATE_TRANSITION'))).toMatch(/O estado da execução mudou/);
    expect(commandErrorMessage(api(429, 'RATE_LIMIT_EXCEEDED', Date.parse('2026-10-21T12:00:30Z')))).toMatch(/Aguarde até/);
    expect(commandErrorMessage(api(429, 'RATE_LIMIT_EXCEEDED'))).toMatch(/Aguarde um instante/);
    expect(commandErrorMessage(new Error('boom'))).toMatch(/Não foi possível concluir a ação/);
  });
});
