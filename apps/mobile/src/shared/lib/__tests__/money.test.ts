import { formatBRL } from '../money';

describe('formatBRL', () => {
  it('formats integer cents as BRL', () => {
    expect(formatBRL(5000)).toBe('R$ 50,00');
    expect(formatBRL(1020)).toBe('R$ 10,20');
    expect(formatBRL(0)).toBe('R$ 0,00');
  });

  it.each([50.5, Number.NaN, Number.POSITIVE_INFINITY, 2 ** 53])('rejects invalid money %p', (cents) => {
    expect(() => formatBRL(cents)).toThrow(RangeError);
  });
});
