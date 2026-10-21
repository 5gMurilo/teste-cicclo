const brl = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });

/** Formats integer cents as BRL. Throws on anything that is not a safe integer: never guess money. */
export function formatBRL(cents: number): string {
  if (!Number.isSafeInteger(cents)) {
    throw new RangeError(`formatBRL expects safe integer cents, received ${cents}`);
  }
  return brl.format(cents / 100);
}
