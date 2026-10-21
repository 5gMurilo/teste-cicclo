import type { Catalog, Service } from '../../catalog/queries';
import type { Wallet } from '../../wallet/queries';
import { checkAdd, deriveSelection, MAX_OCCURRENCES } from '../checkoutSelectors';
import type { CheckoutSelection } from '../checkoutReducer';

const service = (code: string, unitPriceCents: number, over: Partial<Service> = {}): Service => ({
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
const catalog = (...services: Service[]): Catalog => ({ services });
const wallet = (balanceCents: number): Wallet => ({ balanceCents, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' });
const select = (quantities: Record<string, number>): CheckoutSelection => ({
  quantities,
  preferredFirstServiceCode: null,
});

const CATALOG = catalog(service('wash', 1890), service('dry', 2090));

describe('deriveSelection', () => {
  it('derives 3980 selected and 1020 available from a 5000 wallet without touching the balance', () => {
    const derived = deriveSelection(select({ wash: 1, dry: 1 }), CATALOG, wallet(5000));

    expect(derived).toMatchObject({
      serviceCodes: ['wash', 'dry'],
      quantity: 2,
      totalCents: 3980,
      availableCents: 1020,
      confirmedBalanceCents: 5000,
      problems: [],
    });
  });

  it('expands a repeated service into one entry per occurrence', () => {
    const derived = deriveSelection(select({ wash: 2 }), CATALOG, wallet(5000));

    expect(derived.serviceCodes).toEqual(['wash', 'wash']);
    expect(derived.quantity).toBe(2);
    expect(derived.totalCents).toBe(3780);
    expect(derived.availableCents).toBe(1220);
  });

  it('treats an empty selection as zero spent with the full balance available', () => {
    expect(deriveSelection(select({}), CATALOG, wallet(5000))).toMatchObject({
      quantity: 0,
      totalCents: 0,
      availableCents: 5000,
      problems: [],
    });
  });

  it('allows 10 occurrences and flags the 11th as over the limit', () => {
    const cheap = catalog(service('wash', 100));

    expect(deriveSelection(select({ wash: MAX_OCCURRENCES }), cheap, wallet(100_000)).problems).toEqual([]);
    expect(deriveSelection(select({ wash: MAX_OCCURRENCES + 1 }), cheap, wallet(100_000)).problems).toEqual([
      { kind: 'over-limit' },
    ]);
  });

  it('flags a selection above the balance as insufficient', () => {
    const derived = deriveSelection(select({ wash: 1, dry: 1 }), CATALOG, wallet(3979));

    expect(derived.availableCents).toBe(-1);
    expect(derived.problems).toEqual([{ kind: 'insufficient-balance' }]);
  });

  it('never turns missing wallet or catalog data into a valid zero', () => {
    const noWallet = deriveSelection(select({ wash: 1 }), CATALOG, undefined);
    const noCatalog = deriveSelection(select({ wash: 1 }), undefined, wallet(5000));

    for (const derived of [noWallet, noCatalog]) {
      expect(derived.problems).toContainEqual({ kind: 'data-unavailable' });
      expect(derived.availableCents).toBeNull();
    }
    expect(noCatalog.totalCents).toBeNull();
    expect(noWallet.confirmedBalanceCents).toBeNull();
  });

  it.each([-1, 0, 1.5, Number.NaN])('rejects quantity %p without a total', (count) => {
    const derived = deriveSelection(select({ wash: count }), CATALOG, wallet(5000));

    expect(derived.problems).toContainEqual({ kind: 'invalid-quantity', code: 'wash' });
    expect(derived.totalCents).toBeNull();
    expect(derived.serviceCodes).toEqual([]);
  });

  it('flags unknown and unavailable services', () => {
    expect(deriveSelection(select({ iron: 1 }), CATALOG, wallet(5000))).toMatchObject({
      problems: [{ kind: 'unknown-service', code: 'iron' }],
      totalCents: null,
    });

    const off = catalog(service('wash', 1890, { isActive: false, canBeBooked: false, unavailabilityReason: 'SERVICE_DISABLED' }));
    expect(deriveSelection(select({ wash: 1 }), off, wallet(5000)).problems).toEqual([
      { kind: 'service-unavailable', code: 'wash' },
    ]);
  });

  it('follows a price change in the catalog', () => {
    const repriced = catalog(service('wash', 2500), service('dry', 2090));

    expect(deriveSelection(select({ wash: 1, dry: 1 }), repriced, wallet(5000)).totalCents).toBe(4590);
  });

  it('flags totals beyond the safe integer range instead of returning an imprecise number', () => {
    const huge = catalog(service('wash', Number.MAX_SAFE_INTEGER));
    const derived = deriveSelection(select({ wash: 2 }), huge, wallet(5000));

    expect(derived.problems).toContainEqual({ kind: 'overflow' });
    expect(derived.totalCents).toBeNull();
  });
});

describe('checkAdd', () => {
  it('accepts an addition that fits the balance and the limit', () => {
    expect(checkAdd(select({ wash: 1 }), 'dry', CATALOG, wallet(5000))).toEqual({ ok: true });
  });

  it('refuses an addition that would exceed the balance', () => {
    expect(checkAdd(select({ wash: 1, dry: 1 }), 'wash', CATALOG, wallet(5000))).toEqual({
      ok: false,
      reason: 'insufficient-balance',
    });
  });

  it('refuses the 11th occurrence even when the balance would allow it', () => {
    const cheap = catalog(service('wash', 100));

    expect(checkAdd(select({ wash: 10 }), 'wash', cheap, wallet(100_000))).toEqual({ ok: false, reason: 'over-limit' });
  });

  it('checks data and service eligibility before the balance', () => {
    expect(checkAdd(select({}), 'wash', CATALOG, undefined)).toEqual({ ok: false, reason: 'data-unavailable' });
    expect(checkAdd(select({}), 'iron', CATALOG, wallet(0))).toEqual({ ok: false, reason: 'unknown-service' });
    const off = catalog(service('wash', 1890, { canBeBooked: false, unavailabilityReason: 'NO_OPERATIONAL_MACHINE' }));
    expect(checkAdd(select({}), 'wash', off, wallet(0))).toEqual({ ok: false, reason: 'service-unavailable' });
  });
});
