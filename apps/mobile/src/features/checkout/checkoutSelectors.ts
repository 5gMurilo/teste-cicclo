import type { Catalog } from '../catalog/queries';
import type { Wallet } from '../wallet/queries';
import type { CheckoutSelection } from './checkoutReducer';

export const MAX_OCCURRENCES = 10;

export type SelectionProblem =
  | { kind: 'data-unavailable' }
  | { kind: 'invalid-quantity'; code: string }
  | { kind: 'unknown-service'; code: string }
  | { kind: 'service-unavailable'; code: string }
  | { kind: 'over-limit' }
  | { kind: 'overflow' }
  | { kind: 'insufficient-balance' };

export type DerivedSelection = {
  /** One entry per occurrence: quantity 2 of `wash` is `['wash', 'wash']`. */
  serviceCodes: string[];
  quantity: number;
  /** Sum of unit prices in cents, or null when it cannot be computed honestly. */
  totalCents: number | null;
  confirmedBalanceCents: number | null;
  /** Confirmed balance minus the total; may be negative when the selection no longer fits. */
  availableCents: number | null;
  problems: SelectionProblem[];
};

/** Single place where selection, catalog and wallet are combined. Never mutates or debits anything. */
export function deriveSelection(
  selection: CheckoutSelection,
  catalog: Catalog | undefined,
  wallet: Wallet | undefined,
): DerivedSelection {
  const problems: SelectionProblem[] = [];
  const serviceCodes: string[] = [];
  let quantity = 0;
  let total: number | null = 0;

  if (!catalog || !wallet) problems.push({ kind: 'data-unavailable' });

  for (const [code, count] of Object.entries(selection.quantities)) {
    if (!Number.isSafeInteger(count) || count <= 0) {
      problems.push({ kind: 'invalid-quantity', code });
      total = null;
      continue;
    }
    quantity += count;
    for (let i = 0; i < count; i++) serviceCodes.push(code);

    if (!catalog) continue;
    const service = catalog.services.find((s) => s.code === code);
    if (!service) {
      problems.push({ kind: 'unknown-service', code });
      total = null;
      continue;
    }
    if (!service.isActive || !service.canBeBooked) problems.push({ kind: 'service-unavailable', code });
    if (total !== null) total += service.unitPriceCents * count;
  }

  if (!catalog) total = null;
  if (total !== null && !Number.isSafeInteger(total)) {
    problems.push({ kind: 'overflow' });
    total = null;
  }
  if (quantity > MAX_OCCURRENCES) problems.push({ kind: 'over-limit' });

  const confirmedBalanceCents = wallet?.balanceCents ?? null;
  const availableCents = total !== null && confirmedBalanceCents !== null ? confirmedBalanceCents - total : null;
  if (availableCents !== null && availableCents < 0) problems.push({ kind: 'insufficient-balance' });

  return {
    serviceCodes,
    quantity,
    totalCents: total,
    confirmedBalanceCents,
    availableCents,
    problems,
  };
}

export type AddCheck = { ok: true } | { ok: false; reason: SelectionProblem['kind'] };

/** Whether one more unit of `code` may be added; data and service checks come before the balance. */
export function checkAdd(
  selection: CheckoutSelection,
  code: string,
  catalog: Catalog | undefined,
  wallet: Wallet | undefined,
): AddCheck {
  if (!catalog || !wallet) return { ok: false, reason: 'data-unavailable' };

  const service = catalog.services.find((s) => s.code === code);
  if (!service) return { ok: false, reason: 'unknown-service' };
  if (!service.isActive || !service.canBeBooked) return { ok: false, reason: 'service-unavailable' };

  const current = deriveSelection(selection, catalog, wallet);
  if (current.totalCents === null) return { ok: false, reason: 'data-unavailable' };
  if (current.quantity + 1 > MAX_OCCURRENCES) return { ok: false, reason: 'over-limit' };
  if (!Number.isSafeInteger(current.totalCents + service.unitPriceCents)) return { ok: false, reason: 'overflow' };
  if (current.totalCents + service.unitPriceCents > wallet.balanceCents) {
    return { ok: false, reason: 'insufficient-balance' };
  }
  return { ok: true };
}
