export type CheckoutSelection = {
  quantities: Readonly<Record<string, number>>;
  preferredFirstServiceCode: string | null;
};

export type CheckoutAction =
  | { type: 'add'; code: string }
  | { type: 'remove'; code: string }
  | { type: 'setPreference'; code: string | null }
  | { type: 'clear' };

export const emptySelection: CheckoutSelection = { quantities: {}, preferredFirstServiceCode: null };

// Pure local state: limits, prices and balance are checked by the selectors, never here.
export function checkoutReducer(state: CheckoutSelection, action: CheckoutAction): CheckoutSelection {
  switch (action.type) {
    case 'add':
      return {
        ...state,
        quantities: { ...state.quantities, [action.code]: (state.quantities[action.code] ?? 0) + 1 },
      };
    case 'remove': {
      const current = state.quantities[action.code];
      if (!current) return state;
      const { [action.code]: _removed, ...rest } = state.quantities;
      const quantities = current > 1 ? { ...rest, [action.code]: current - 1 } : rest;
      const lostPreferred = current === 1 && state.preferredFirstServiceCode === action.code;
      return {
        quantities,
        preferredFirstServiceCode: lostPreferred ? null : state.preferredFirstServiceCode,
      };
    }
    case 'setPreference':
      if (action.code !== null && !state.quantities[action.code]) return state;
      return { ...state, preferredFirstServiceCode: action.code };
    case 'clear':
      return emptySelection;
  }
}
