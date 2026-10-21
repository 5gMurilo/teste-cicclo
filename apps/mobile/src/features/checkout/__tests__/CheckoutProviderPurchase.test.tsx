import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react-native';
import type { ReactNode } from 'react';

import { createTestQueryClient } from '../../../test/queryClient';
import { CheckoutProvider, useCheckout } from '../CheckoutProvider';

// The purchase capability is on: these tests guard what confirm() must refuse even when the UI cannot reach it.
jest.mock('../../../shared/api/capabilities', () => ({ capabilities: { purchase: true, keyRetentionMs: null } }));

let client: QueryClient;
let posts: string[];

const respond = (body: unknown) =>
  Promise.resolve({
    status: 200,
    ok: true,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  client = createTestQueryClient();
  posts = [];
  globalThis.fetch = jest.fn((url: string | URL | Request, init?: RequestInit) => {
    if (init?.method === 'POST') {
      posts.push(String(url));
      return respond({});
    }
    return respond(
      String(url).endsWith('/wallet')
        ? { balanceCents: 5000, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' }
        : {
            services: [
              { code: 'wash', name: 'Lavagem', machineType: 'washer', unitPriceCents: 1890, currency: 'BRL', isActive: true, canBeBooked: true, unavailabilityReason: null },
            ],
          },
    );
  }) as unknown as typeof fetch;
});

afterEach(() => client.clear());

const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={client}>
    <CheckoutProvider>{children}</CheckoutProvider>
  </QueryClientProvider>
);

describe('confirm() with the purchase capability on', () => {
  it('is enabled with data ready but refuses an empty selection: no request, still idle', async () => {
    const { result } = await renderHook(() => useCheckout(), { wrapper });
    await waitFor(() => expect(result.current.readiness).toBe('ready'));

    expect(result.current.purchase.enabled).toBe(true);
    expect(result.current.purchase.canConfirm).toBe(false);
    await act(async () => result.current.purchase.confirm());

    expect(result.current.purchase.state.status).toBe('idle');
    expect(posts).toHaveLength(0);
  });

  it('allows the purchase once a valid selection exists', async () => {
    const { result } = await renderHook(() => useCheckout(), { wrapper });
    await waitFor(() => expect(result.current.readiness).toBe('ready'));

    await act(async () => {
      result.current.add('wash');
    });

    expect(result.current.purchase.canConfirm).toBe(true);
  });
});
