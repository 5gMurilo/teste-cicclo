import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { act, fireEvent, renderHook, waitFor } from '@testing-library/react-native';
import { renderRouter, screen } from 'expo-router/testing-library';
import type { ReactNode } from 'react';
import { Pressable, Text } from 'react-native';

import { queryClient as appQueryClient } from '../../../shared/api/queryClient';
import { createTestQueryClient } from '../../../test/queryClient';
import { CheckoutProvider, useCheckout } from '../CheckoutProvider';

const CHECKOUT_TAB = /^Checkout.*tab, 3 of 3$/;
const service = (code: string, unitPriceCents: number, over: Record<string, unknown> = {}) => ({
  code,
  name: code,
  machineType: code === 'dry' ? 'dryer' : 'washer',
  unitPriceCents,
  currency: 'BRL',
  isActive: true,
  canBeBooked: true,
  unavailabilityReason: null,
  ...over,
});
const wallet = (balanceCents: number) => ({ balanceCents, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' });

type Reply = { status: number; body: unknown };
let walletReply: Reply;
let servicesReply: Reply;

function respond({ status, body }: Reply): Promise<Response> {
  return Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);
}

function setApi(balanceCents: number, ...services: ReturnType<typeof service>[]) {
  walletReply = { status: 200, body: wallet(balanceCents) };
  servicesReply = { status: 200, body: { services } };
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  globalThis.fetch = jest.fn((url: string | URL | Request) =>
    respond(String(url).endsWith('/wallet') ? walletReply : servicesReply),
  ) as unknown as typeof fetch;
  setApi(5000, service('wash', 1890), service('dry', 2090));
});

async function mountProvider(client: QueryClient = createTestQueryClient()) {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>
      <CheckoutProvider>{children}</CheckoutProvider>
    </QueryClientProvider>
  );
  return renderHook(() => useCheckout(), { wrapper });
}

describe('CheckoutProvider', () => {
  it('keeps the selection while the user switches tabs', async () => {
    function AddWash() {
      const checkout = useCheckout();
      return (
        <>
          <Text>{`readiness ${checkout.readiness}`}</Text>
          <Pressable accessibilityRole="button" onPress={() => checkout.add('wash')}>
            <Text>add wash</Text>
          </Pressable>
        </>
      );
    }
    function Quantity() {
      return <Text>{`quantity ${useCheckout().derived.quantity}`}</Text>;
    }
    try {
      await renderRouter({
        appDir: './src/app',
        overrides: { '(tabs)/index': AddWash, '(tabs)/checkout': Quantity },
      });
      expect(await screen.findByText('readiness ready')).toBeOnTheScreen();

      await fireEvent.press(screen.getByRole('button', { name: 'add wash' }));
      await fireEvent.press(screen.getByLabelText(CHECKOUT_TAB));

      expect(screen.getByText('quantity 1')).toBeOnTheScreen();
    } finally {
      appQueryClient.clear();
    }
  });

  it('refuses an addition that exceeds the balance and keeps the previous selection', async () => {
    setApi(2000, service('wash', 1890), service('dry', 2090));
    const { result } = await mountProvider();
    await waitFor(() => expect(result.current.readiness).toBe('ready'));

    let first: unknown;
    let second: unknown;
    await act(async () => {
      first = result.current.add('wash');
      second = result.current.add('wash');
    });

    expect(first).toEqual({ ok: true });
    expect(second).toEqual({ ok: false, reason: 'insufficient-balance' });
    expect(result.current.selection.quantities).toEqual({ wash: 1 });
    expect(result.current.derived.availableCents).toBe(110);
  });

  it('blocks additions while the wallet is loading and after a wallet error', async () => {
    const { result } = await mountProvider();
    expect(result.current.readiness).toBe('loading');
    await act(async () => {
      expect(result.current.add('wash')).toEqual({ ok: false, reason: 'data-unavailable' });
    });

    walletReply = { status: 404, body: { errorCode: 'NOT_FOUND' } };
    const failed = await mountProvider();
    await waitFor(() => expect(failed.result.current.readiness).toBe('unavailable'));
    await act(async () => {
      expect(failed.result.current.add('wash')).toEqual({ ok: false, reason: 'data-unavailable' });
    });
    expect(failed.result.current.selection.quantities).toEqual({});
  });

  it('blocks an unavailable service', async () => {
    setApi(5000, service('wash', 1890, { isActive: false, canBeBooked: false, unavailabilityReason: 'SERVICE_DISABLED' }));
    const { result } = await mountProvider();
    await waitFor(() => expect(result.current.readiness).toBe('ready'));

    await act(async () => {
      expect(result.current.add('wash')).toEqual({ ok: false, reason: 'service-unavailable' });
    });
    expect(result.current.selection.quantities).toEqual({});
  });

  it('accepts 10 occurrences and blocks the 11th', async () => {
    setApi(100_000, service('wash', 100));
    const { result } = await mountProvider();
    await waitFor(() => expect(result.current.readiness).toBe('ready'));

    const outcomes: unknown[] = [];
    await act(async () => {
      for (let i = 0; i < 11; i++) outcomes.push(result.current.add('wash'));
    });

    expect(outcomes.slice(0, 10).every((o) => (o as { ok: boolean }).ok)).toBe(true);
    expect(outcomes[10]).toEqual({ ok: false, reason: 'over-limit' });
    expect(result.current.derived.quantity).toBe(10);
  });

  it('still allows removal and clears the preference with the last preferred unit', async () => {
    const { result } = await mountProvider();
    await waitFor(() => expect(result.current.readiness).toBe('ready'));
    await act(async () => {
      result.current.add('wash');
      result.current.add('dry');
      result.current.setPreference('wash');
    });
    expect(result.current.selection.preferredFirstServiceCode).toBe('wash');

    await act(async () => {
      result.current.remove('wash');
    });

    expect(result.current.selection).toEqual({ quantities: { dry: 1 }, preferredFirstServiceCode: null });
  });
});
