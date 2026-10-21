import { readPublicEnv } from '../config/env';
import { parseRetryAfterDeadline, toApiError } from './apiError';

const API_PREFIX = '/api/v1';
const REQUEST_TIMEOUT_MS = 15_000;

export type TransportErrorKind = 'network' | 'timeout' | 'invalid-body';

/** A failure with no usable HTTP error response; `status` is set only when a 2xx body was unusable. */
export class TransportError extends Error {
  readonly kind: TransportErrorKind;
  readonly status: number | null;

  constructor(kind: TransportErrorKind, status: number | null = null) {
    super(`Transport failure: ${kind}`);
    this.name = 'TransportError';
    this.kind = kind;
    this.status = status;
  }
}

export type RequestOptions = {
  method: 'GET' | 'POST';
  /** Path under the API prefix, starting with `/`. */
  path: string;
  /** Already serialized: the transport never re-encodes, so retries stay byte-identical. */
  body?: string;
  headers?: Record<string, string>;
  signal?: AbortSignal;
};

function parseJson(text: string): { ok: true; value: unknown } | { ok: false } {
  try {
    return { ok: true, value: JSON.parse(text) };
  } catch {
    return { ok: false };
  }
}

export async function requestJson(options: RequestOptions): Promise<unknown> {
  const { apiOrigin } = readPublicEnv();
  const controller = new AbortController();
  let timedOut = false;

  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, REQUEST_TIMEOUT_MS);
  const forwardAbort = () => controller.abort();
  const external = options.signal;
  if (external?.aborted) controller.abort();
  external?.addEventListener('abort', forwardAbort);

  const headers: Record<string, string> = { Accept: 'application/json', ...options.headers };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';

  try {
    let response: Response;
    let text: string;
    try {
      response = await fetch(`${apiOrigin}${API_PREFIX}${options.path}`, {
        method: options.method,
        headers,
        body: options.body,
        signal: controller.signal,
      });
      text = await response.text();
    } catch (error) {
      if (timedOut) throw new TransportError('timeout');
      if (external?.aborted) throw error;
      throw new TransportError('network');
    }

    const parsed = text === '' ? { ok: true as const, value: null } : parseJson(text);

    if (!response.ok) {
      throw toApiError(
        response.status,
        parsed.ok ? parsed.value : null,
        parseRetryAfterDeadline(response.headers.get('Retry-After'), Date.now()),
      );
    }
    if (!parsed.ok) throw new TransportError('invalid-body', response.status);
    return parsed.value;
  } finally {
    clearTimeout(timer);
    external?.removeEventListener('abort', forwardAbort);
  }
}
