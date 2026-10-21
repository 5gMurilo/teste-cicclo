import { checkoutReducer, emptySelection, type CheckoutSelection } from '../checkoutReducer';

function freeze(selection: CheckoutSelection): CheckoutSelection {
  return Object.freeze({ ...selection, quantities: Object.freeze({ ...selection.quantities }) });
}

describe('checkoutReducer', () => {
  it('adds a first and a repeated unit of the same service, preserving duplicates', () => {
    const once = checkoutReducer(emptySelection, { type: 'add', code: 'wash' });
    const twice = checkoutReducer(once, { type: 'add', code: 'wash' });

    expect(once.quantities).toEqual({ wash: 1 });
    expect(twice.quantities).toEqual({ wash: 2 });
  });

  it('removes one unit at a time and drops the key at zero', () => {
    const state = freeze({ quantities: { wash: 2, dry: 1 }, preferredFirstServiceCode: null });

    const afterOne = checkoutReducer(state, { type: 'remove', code: 'wash' });
    const afterDry = checkoutReducer(afterOne, { type: 'remove', code: 'dry' });

    expect(afterOne.quantities).toEqual({ wash: 1, dry: 1 });
    expect(afterDry.quantities).toEqual({ wash: 1 });
    expect(Object.keys(afterDry.quantities)).not.toContain('dry');
  });

  it('ignores removing a service that is not selected', () => {
    const state = freeze({ quantities: { wash: 1 }, preferredFirstServiceCode: null });

    expect(checkoutReducer(state, { type: 'remove', code: 'dry' })).toBe(state);
  });

  it('keeps a preference only for a selected service', () => {
    const state = freeze({ quantities: { wash: 1, dry: 1 }, preferredFirstServiceCode: null });

    expect(checkoutReducer(state, { type: 'setPreference', code: 'dry' }).preferredFirstServiceCode).toBe('dry');
    expect(checkoutReducer(state, { type: 'setPreference', code: 'iron' }).preferredFirstServiceCode).toBeNull();
    const preferred = checkoutReducer(state, { type: 'setPreference', code: 'dry' });
    expect(checkoutReducer(preferred, { type: 'setPreference', code: null }).preferredFirstServiceCode).toBeNull();
  });

  it('clears the preference when its last unit is removed, but not while units remain', () => {
    const state = freeze({ quantities: { wash: 2, dry: 1 }, preferredFirstServiceCode: 'wash' });

    const oneLeft = checkoutReducer(state, { type: 'remove', code: 'wash' });
    const noneLeft = checkoutReducer(oneLeft, { type: 'remove', code: 'wash' });

    expect(oneLeft.preferredFirstServiceCode).toBe('wash');
    expect(noneLeft.preferredFirstServiceCode).toBeNull();
    expect(noneLeft.quantities).toEqual({ dry: 1 });
  });

  it('clear returns an empty selection', () => {
    const state = freeze({ quantities: { wash: 2 }, preferredFirstServiceCode: 'wash' });

    expect(checkoutReducer(state, { type: 'clear' })).toEqual({ quantities: {}, preferredFirstServiceCode: null });
  });

  it('never mutates its input', () => {
    const state = freeze({ quantities: { wash: 1 }, preferredFirstServiceCode: 'wash' });

    expect(() => {
      checkoutReducer(state, { type: 'add', code: 'dry' });
      checkoutReducer(state, { type: 'remove', code: 'wash' });
      checkoutReducer(state, { type: 'setPreference', code: null });
      checkoutReducer(state, { type: 'clear' });
    }).not.toThrow();
    expect(state).toEqual({ quantities: { wash: 1 }, preferredFirstServiceCode: 'wash' });
  });
});
