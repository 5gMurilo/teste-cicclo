/**
 * Backend capabilities that are only trusted once proven against the real API (gates in tasks.md).
 * Flip a value in the commit that records its proof in docs/api-contract.md, never before.
 */
export const capabilities = {
  /**
   * C2, proven on 2026-10-21 against the real API (live/contract.live.test.ts): 201 then 200 replay with the
   * same booking and no second debit, IDEMPOTENCY_CONFLICT, definitive refusals, 429 with Retry-After and the
   * next read already showing the debited balance. See docs/api-contract.md.
   */
  purchase: true,
  /**
   * How long the server replays a stored Idempotency-Key. null = no expiry known: the key is stored with the
   * booking behind a unique index and the backend has no cleanup, so a replay stays valid while the booking exists.
   */
  keyRetentionMs: null as number | null,
};
