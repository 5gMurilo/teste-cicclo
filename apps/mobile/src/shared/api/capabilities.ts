/**
 * Backend capabilities that are only trusted once proven against the real API (gates in tasks.md).
 * Flip a value in the commit that records its proof in docs/api-contract.md, never before.
 */
export const capabilities = {
  /** C2: POST/replay 201/200 with the same id, duplicates, limit, error codes and money consistency proven. */
  purchase: false,
  /** C2: how long the server replays a stored Idempotency-Key; null while unproven. */
  keyRetentionMs: null as number | null,
};
