import { ApiError, parseRetryAfterDeadline, toApiError } from '../apiError';

describe('toApiError', () => {
  it('preserves status, errorCode and traceId from a ProblemDetails body', () => {
    const error = toApiError(422, {
      type: 'urn:cicclo:problem:price-changed',
      title: 'Preço alterado',
      status: 422,
      detail: 'texto livre',
      errorCode: 'PRICE_CHANGED',
      traceId: '00-abc123-01',
    });

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(422);
    expect(error.errorCode).toBe('PRICE_CHANGED');
    expect(error.traceId).toBe('00-abc123-01');
  });

  it.each([
    ['null', null],
    ['empty string', ''],
    ['html', '<html>502 Bad Gateway</html>'],
    ['array', []],
    ['wrong field types', { errorCode: 42, traceId: { id: 1 } }],
    ['unsafe characters', { errorCode: 'BAD CODE\n<script>', traceId: 'x'.repeat(500) }],
  ])('keeps the status and drops unusable fields for a %s body', (_name, body) => {
    const error = toApiError(503, body);

    expect(error.status).toBe(503);
    expect(error.errorCode).toBeNull();
    expect(error.traceId).toBeNull();
  });

  it('does not let detail change the classification or leak into the message', () => {
    const a = toApiError(409, { errorCode: 'IDEMPOTENCY_CONFLICT', detail: 'um texto' });
    const b = toApiError(409, { errorCode: 'IDEMPOTENCY_CONFLICT', detail: 'outro texto' });

    expect([a.status, a.errorCode, a.traceId]).toEqual([b.status, b.errorCode, b.traceId]);
    expect(a.message).not.toContain('um texto');
  });

  it('attaches the Retry-After deadline when one is provided', () => {
    expect(toApiError(429, { errorCode: 'RATE_LIMITED' }, 1_700_000_030_000).retryAfterAt).toBe(
      1_700_000_030_000,
    );
    expect(toApiError(429, null).retryAfterAt).toBeNull();
  });
});

describe('parseRetryAfterDeadline', () => {
  const now = Date.parse('2026-10-21T12:00:00Z');

  it('turns delay-seconds into an absolute deadline', () => {
    expect(parseRetryAfterDeadline('30', now)).toBe(now + 30_000);
    expect(parseRetryAfterDeadline('0', now)).toBe(now);
  });

  it('accepts an HTTP-date and keeps a past date as an already expired deadline', () => {
    expect(parseRetryAfterDeadline('Wed, 21 Oct 2026 12:01:00 GMT', now)).toBe(
      Date.parse('2026-10-21T12:01:00Z'),
    );
    expect(parseRetryAfterDeadline('Wed, 21 Oct 2026 11:00:00 GMT', now)).toBe(
      Date.parse('2026-10-21T11:00:00Z'),
    );
  });

  it.each([null, undefined, '', 'soon', '-5', '1.5', '10s', 'Wed, 99 Foo 2026 99:99:99 GMT'])(
    'returns null for an absent or invalid value (%p)',
    (value) => {
      expect(parseRetryAfterDeadline(value, now)).toBeNull();
    },
  );
});
