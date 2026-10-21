import { z } from 'zod';

const ERROR_CODE = /^[A-Za-z0-9_.-]{1,64}$/;
const TRACE_ID = /^[A-Za-z0-9_.:-]{1,128}$/;
const HTTP_DATE = /^[A-Za-z]{3}, \d{2} [A-Za-z]{3} \d{4} \d{2}:\d{2}:\d{2} GMT$/;
const DELAY_SECONDS = /^\d+$/;

// Only the fields that drive behavior are read; `detail` and `title` are deliberately ignored.
const problemDetailsSchema = z.object({
  errorCode: z.string().regex(ERROR_CODE).nullish().catch(null),
  traceId: z.string().regex(TRACE_ID).nullish().catch(null),
});

export class ApiError extends Error {
  readonly status: number;
  readonly errorCode: string | null;
  readonly traceId: string | null;
  /** Absolute epoch-ms deadline from Retry-After, or null when absent or invalid. */
  readonly retryAfterAt: number | null;

  constructor(init: {
    status: number;
    errorCode: string | null;
    traceId: string | null;
    retryAfterAt: number | null;
  }) {
    super(`HTTP ${init.status}`);
    this.name = 'ApiError';
    this.status = init.status;
    this.errorCode = init.errorCode;
    this.traceId = init.traceId;
    this.retryAfterAt = init.retryAfterAt;
  }
}

export function parseRetryAfterDeadline(
  value: string | null | undefined,
  nowMs: number,
): number | null {
  const raw = value?.trim();
  if (!raw) return null;

  if (DELAY_SECONDS.test(raw)) {
    const seconds = Number(raw);
    return Number.isSafeInteger(seconds) ? nowMs + seconds * 1000 : null;
  }

  if (HTTP_DATE.test(raw)) {
    const at = Date.parse(raw);
    return Number.isNaN(at) ? null : at;
  }

  return null;
}

export function toApiError(
  status: number,
  body: unknown,
  retryAfterAt: number | null = null,
): ApiError {
  const parsed = problemDetailsSchema.safeParse(body);
  return new ApiError({
    status,
    errorCode: (parsed.success && parsed.data.errorCode) || null,
    traceId: (parsed.success && parsed.data.traceId) || null,
    retryAfterAt,
  });
}
