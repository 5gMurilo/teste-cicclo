import { useQuery } from '@tanstack/react-query';
import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';

import { capabilities } from '../../shared/api/capabilities';
import { catalogOptions } from '../catalog/queries';
import { walletOptions } from '../wallet/queries';
import {
  checkoutReducer,
  emptySelection,
  type CheckoutAction,
  type CheckoutSelection,
} from './checkoutReducer';
import { checkAdd, deriveSelection, type AddCheck, type DerivedSelection } from './checkoutSelectors';
import { useBookingSubmission, type SubmissionState } from './useBookingSubmission';

/** `unavailable`: a read failed or has no data; additions are blocked until both reads succeed. */
export type CheckoutReadiness = 'loading' | 'unavailable' | 'ready';

export type PriceChange = { code: string; name: string; fromCents: number; toCents: number };

export type AddResult = AddCheck | { ok: false; reason: 'purchase-pending' };

export type PurchaseControls = {
  /** False until the real API proves the purchase contract (capabilities.purchase). */
  enabled: boolean;
  state: SubmissionState;
  /** Ready, valid, reviewed and not already in flight. */
  canConfirm: boolean;
  confirm: () => Promise<void>;
  verify: () => Promise<void>;
  dismiss: () => void;
};

type CheckoutContextValue = {
  selection: CheckoutSelection;
  derived: DerivedSelection;
  readiness: CheckoutReadiness;
  /** Selected services whose catalog price differs from the one the user saw when selecting. */
  priceChanges: PriceChange[];
  add: (code: string) => AddResult;
  remove: (code: string) => void;
  setPreference: (code: string | null) => void;
  clear: () => void;
  purchase: PurchaseControls;
  /** Explicit new review: accepts the current catalog prices as the ones the user has seen. */
  acknowledgePriceChanges: () => void;
};

type Store = {
  selection: CheckoutSelection;
  /** Unit price seen per selected service code. */
  seenPrices: Readonly<Record<string, number>>;
};

const EMPTY_STORE: Store = { selection: emptySelection, seenPrices: {} };

const CheckoutContext = createContext<CheckoutContextValue | null>(null);

function pruneSeenPrices(store: Store): Store {
  const seenPrices = Object.fromEntries(
    Object.entries(store.seenPrices).filter(([code]) => store.selection.quantities[code]),
  );
  return { ...store, seenPrices };
}

export function CheckoutProvider({ children }: { children: ReactNode }) {
  const wallet = useQuery(walletOptions());
  const catalog = useQuery(catalogOptions());

  // The ref is the synchronous source of truth so two taps in one tick are validated one after the other.
  const storeRef = useRef<Store>(EMPTY_STORE);
  const [store, setStore] = useState<Store>(EMPTY_STORE);

  const readiness: CheckoutReadiness =
    wallet.isError || catalog.isError || (!wallet.isPending && !wallet.data) || (!catalog.isPending && !catalog.data)
      ? 'unavailable'
      : wallet.isPending || catalog.isPending
        ? 'loading'
        : 'ready';

  const commit = useCallback((next: Store) => {
    storeRef.current = next;
    setStore(next);
  }, []);

  const dispatch = useCallback(
    (action: CheckoutAction) => {
      const current = storeRef.current;
      commit(pruneSeenPrices({ ...current, selection: checkoutReducer(current.selection, action) }));
    },
    [commit],
  );

  // The selection is cleared only by a confirmed purchase, never by a rejection or an unknown outcome.
  const clearAfterPurchase = useCallback(() => dispatch({ type: 'clear' }), [dispatch]);
  const submission = useBookingSubmission({
    keyRetentionMs: capabilities.keyRetentionMs,
    onConfirmed: clearAfterPurchase,
  });
  const purchasePending = submission.state.status === 'submitting' || submission.state.status === 'unknown';

  const add = useCallback(
    (code: string): AddResult => {
      if (purchasePending) return { ok: false, reason: 'purchase-pending' };
      if (readiness !== 'ready') return { ok: false, reason: 'data-unavailable' };
      const current = storeRef.current;
      const check = checkAdd(current.selection, code, catalog.data, wallet.data);
      if (!check.ok) return check;

      const price = catalog.data?.services.find((s) => s.code === code)?.unitPriceCents;
      commit({
        selection: checkoutReducer(current.selection, { type: 'add', code }),
        seenPrices: price === undefined ? current.seenPrices : { [code]: price, ...current.seenPrices },
      });
      return check;
    },
    [commit, purchasePending, readiness, catalog.data, wallet.data],
  );

  const value = useMemo<CheckoutContextValue>(() => {
    const derived = deriveSelection(store.selection, catalog.data, wallet.data);
    const priceChanges = derived.lines.flatMap((line): PriceChange[] => {
      const seen = store.seenPrices[line.code];
      return seen !== undefined && line.unitPriceCents !== null && line.unitPriceCents !== seen
        ? [{ code: line.code, name: line.name, fromCents: seen, toCents: line.unitPriceCents }]
        : [];
    });
    const canConfirm =
      capabilities.purchase &&
      readiness === 'ready' &&
      submission.state.status === 'idle' &&
      derived.quantity > 0 &&
      derived.totalCents !== null &&
      derived.problems.length === 0 &&
      priceChanges.length === 0;

    return {
      selection: store.selection,
      derived,
      readiness,
      priceChanges,
      add,
      // Editing is frozen while a purchase is in flight or unresolved: that intent is immutable.
      remove: (code) => {
        if (!purchasePending) dispatch({ type: 'remove', code });
      },
      setPreference: (code) => {
        if (!purchasePending) dispatch({ type: 'setPreference', code });
      },
      clear: () => {
        if (!purchasePending) dispatch({ type: 'clear' });
      },
      purchase: {
        enabled: capabilities.purchase,
        state: submission.state,
        canConfirm,
        confirm: async () => {
          if (!canConfirm || derived.totalCents === null) return;
          await submission.submit({
            serviceCodes: derived.serviceCodes,
            expectedTotalCents: derived.totalCents,
            preferredFirstServiceCode: store.selection.preferredFirstServiceCode,
          });
        },
        verify: submission.verify,
        dismiss: submission.dismiss,
      },
      acknowledgePriceChanges: () => {
        const current = storeRef.current;
        const seenPrices = Object.fromEntries(
          derived.lines.flatMap((line) => (line.unitPriceCents === null ? [] : [[line.code, line.unitPriceCents]])),
        );
        commit({ ...current, seenPrices: { ...current.seenPrices, ...seenPrices } });
      },
    };
  }, [store, catalog.data, wallet.data, readiness, add, dispatch, commit, submission, purchasePending]);

  return <CheckoutContext.Provider value={value}>{children}</CheckoutContext.Provider>;
}

export function useCheckout(): CheckoutContextValue {
  const value = useContext(CheckoutContext);
  if (!value) throw new Error('useCheckout must be used inside CheckoutProvider');
  return value;
}
