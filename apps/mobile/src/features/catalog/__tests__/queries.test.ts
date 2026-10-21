import type { QueryClient } from '@tanstack/react-query';

import { createTestQueryClient } from '../../../test/queryClient';
import { catalogKey, catalogOptions } from '../queries';

const WASH = {
  code: 'wash',
  name: 'Lavagem',
  machineType: 'washer',
  unitPriceCents: 1890,
  currency: 'BRL',
  isActive: true,
  canBeBooked: true,
  unavailabilityReason: null,
};
const DRY = { ...WASH, code: 'dry', name: 'Secagem', machineType: 'dryer', unitPriceCents: 2090 };

function reply(body: unknown): Promise<Response> {
  return Promise.resolve({
    status: 200,
    ok: true,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);
}

let client: QueryClient;
let signals: AbortSignal[];

function serve(body: unknown) {
  globalThis.fetch = jest.fn(async (_url: string | URL | Request, init?: RequestInit) => {
    signals.push(init?.signal as AbortSignal);
    return reply(body);
  }) as unknown as typeof fetch;
}

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  client = createTestQueryClient();
  signals = [];
});

afterEach(() => client.clear());

describe('catalogOptions', () => {
  it('returns names and prices from the response, whatever they are', async () => {
    serve({ services: [{ ...DRY, unitPriceCents: 2500 }, WASH] });

    const catalog = await client.fetchQuery(catalogOptions());

    expect(catalog.services.map((s) => [s.code, s.name, s.unitPriceCents])).toEqual([
      ['dry', 'Secagem', 2500],
      ['wash', 'Lavagem', 1890],
    ]);
    expect(client.getQueryData(catalogKey)).toEqual(catalog);
    expect(signals[0]).toBeInstanceOf(AbortSignal);
  });

  it('accepts an empty catalog as a valid answer', async () => {
    serve({ services: [] });

    await expect(client.fetchQuery(catalogOptions())).resolves.toEqual({ services: [] });
  });

  it('preserves code, deactivation and the unavailability reason of an unbookable service', async () => {
    const disabled = {
      ...WASH,
      isActive: false,
      canBeBooked: false,
      unavailabilityReason: 'SERVICE_DISABLED',
    };
    serve({ services: [disabled, DRY] });

    const { services } = await client.fetchQuery(catalogOptions());

    expect(services[0]).toMatchObject({
      code: 'wash',
      isActive: false,
      canBeBooked: false,
      unavailabilityReason: 'SERVICE_DISABLED',
    });
    expect(services[1].canBeBooked).toBe(true);
  });

  it.each([
    ['missing name', { services: [{ ...WASH, name: undefined }] }],
    ['zero price', { services: [{ ...WASH, unitPriceCents: 0 }] }],
    ['fractional price', { services: [{ ...WASH, unitPriceCents: 18.9 }] }],
    ['price as string', { services: [{ ...WASH, unitPriceCents: '1890' }] }],
    ['unknown machine type', { services: [{ ...WASH, machineType: 'oven' }] }],
    ['bookable with a reason', { services: [{ ...WASH, unavailabilityReason: 'SERVICE_DISABLED' }] }],
    ['duplicate codes', { services: [WASH, WASH] }],
    ['not a list', { services: null }],
  ])('rejects %s instead of rendering a partial catalog', async (_name, body) => {
    serve(body);

    await expect(client.fetchQuery(catalogOptions())).rejects.toThrow();
    expect(client.getQueryData(catalogKey)).toBeUndefined();
  });
});
