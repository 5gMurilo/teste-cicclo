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

import { catalogOptions } from '../catalog/queries';
import { walletOptions } from '../wallet/queries';
import {
  checkoutReducer,
  emptySelection,
  type CheckoutAction,
  type CheckoutSelection,
} from './checkoutReducer';
import { checkAdd, deriveSelection, type AddCheck, type DerivedSelection } from './checkoutSelectors';

/** `unavailable`: a read failed or has no data; additions are blocked until both reads succeed. */
export type CheckoutReadiness = 'loading' | 'unavailable' | 'ready';

export type PriceChange = { code: string; name: string; fromCents: number; toCents: number };

type CheckoutContextValue = {
  selection: CheckoutSelection;
  derived: DerivedSelection;
  readiness: CheckoutReadiness;
  /** Selected services whose catalog price differs from the one the user saw when selecting. */
  priceChanges: PriceChange[];
  add: (code: string) => AddCheck;
  remove: (code: string) => void;
  setPreference: (code: string | null) => void;
  clear: () => void;
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

  const add = useCallback(
    (code: string): AddCheck => {
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
    [commit, readiness, catalog.data, wallet.data],
  );

  const value = useMemo<CheckoutContextValue>(() => {
    const derived = deriveSelection(store.selection, catalog.data, wallet.data);
    const priceChanges = derived.lines.flatMap((line): PriceChange[] => {
      const seen = store.seenPrices[line.code];
      return seen !== undefined && line.unitPriceCents !== null && line.unitPriceCents !== seen
        ? [{ code: line.code, name: line.name, fromCents: seen, toCents: line.unitPriceCents }]
        : [];
    });
    return {
      selection: store.selection,
      derived,
      readiness,
      priceChanges,
      add,
      remove: (code) => dispatch({ type: 'remove', code }),
      setPreference: (code) => dispatch({ type: 'setPreference', code }),
      clear: () => dispatch({ type: 'clear' }),
      acknowledgePriceChanges: () => {
        const current = storeRef.current;
        const seenPrices = Object.fromEntries(
          derived.lines.flatMap((line) => (line.unitPriceCents === null ? [] : [[line.code, line.unitPriceCents]])),
        );
        commit({ ...current, seenPrices: { ...current.seenPrices, ...seenPrices } });
      },
    };
  }, [store, catalog.data, wallet.data, readiness, add, dispatch, commit]);

  return <CheckoutContext.Provider value={value}>{children}</CheckoutContext.Provider>;
}

export function useCheckout(): CheckoutContextValue {
  const value = useContext(CheckoutContext);
  if (!value) throw new Error('useCheckout must be used inside CheckoutProvider');
  return value;
}
