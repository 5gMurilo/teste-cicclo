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

type CheckoutContextValue = {
  selection: CheckoutSelection;
  derived: DerivedSelection;
  readiness: CheckoutReadiness;
  add: (code: string) => AddCheck;
  remove: (code: string) => void;
  setPreference: (code: string | null) => void;
  clear: () => void;
};

const CheckoutContext = createContext<CheckoutContextValue | null>(null);

export function CheckoutProvider({ children }: { children: ReactNode }) {
  const wallet = useQuery(walletOptions());
  const catalog = useQuery(catalogOptions());

  // The ref is the synchronous source of truth so two taps in one tick are validated one after the other.
  const selectionRef = useRef<CheckoutSelection>(emptySelection);
  const [selection, setSelection] = useState<CheckoutSelection>(emptySelection);

  const readiness: CheckoutReadiness =
    wallet.isError || catalog.isError || (!wallet.isPending && !wallet.data) || (!catalog.isPending && !catalog.data)
      ? 'unavailable'
      : wallet.isPending || catalog.isPending
        ? 'loading'
        : 'ready';

  const dispatch = useCallback((action: CheckoutAction) => {
    selectionRef.current = checkoutReducer(selectionRef.current, action);
    setSelection(selectionRef.current);
  }, []);

  const add = useCallback(
    (code: string): AddCheck => {
      if (readiness !== 'ready') return { ok: false, reason: 'data-unavailable' };
      const check = checkAdd(selectionRef.current, code, catalog.data, wallet.data);
      if (check.ok) dispatch({ type: 'add', code });
      return check;
    },
    [dispatch, readiness, catalog.data, wallet.data],
  );

  const value = useMemo<CheckoutContextValue>(
    () => ({
      selection,
      derived: deriveSelection(selection, catalog.data, wallet.data),
      readiness,
      add,
      remove: (code) => dispatch({ type: 'remove', code }),
      setPreference: (code) => dispatch({ type: 'setPreference', code }),
      clear: () => dispatch({ type: 'clear' }),
    }),
    [selection, catalog.data, wallet.data, readiness, add, dispatch],
  );

  return <CheckoutContext.Provider value={value}>{children}</CheckoutContext.Provider>;
}

export function useCheckout(): CheckoutContextValue {
  const value = useContext(CheckoutContext);
  if (!value) throw new Error('useCheckout must be used inside CheckoutProvider');
  return value;
}
