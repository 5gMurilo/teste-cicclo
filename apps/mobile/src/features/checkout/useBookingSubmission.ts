import { useQueryClient } from '@tanstack/react-query';
import { randomUUID } from 'expo-crypto';
import { useCallback, useRef, useState } from 'react';
import { ZodError } from 'zod';

import { ApiError } from '../../shared/api/apiError';
import { TransportError } from '../../shared/api/client';
import { ConfigError } from '../../shared/config/env';
import { catalogKey } from '../catalog/queries';
import { walletKey } from '../wallet/queries';
import {
  createBooking,
  serializeBookingBody,
  type BookingRequest,
  type ConfirmedBooking,
} from './createBooking';

export type BookingIntent = {
  key: string;
  /** Serialized once; every attempt of this intent sends exactly these bytes. */
  body: string;
  request: Readonly<BookingRequest>;
  createdAt: string;
};

/** Refusals after which the API guarantees nothing was debited and the key was not consumed. */
export type RejectionReason =
  | 'invalid-request'
  | 'service-not-found'
  | 'insufficient-balance'
  | 'price-changed'
  | 'service-disabled'
  | 'no-operational-machine'
  | 'request-too-large'
  | 'unsupported-media-type'
  | 'rate-limited'
  | 'configuration';

/** Why the outcome of a sent purchase cannot be known from the response alone. */
export type UnknownCause =
  | 'network'
  | 'timeout'
  | 'server-error'
  | 'invalid-response'
  | 'unclassified-error'
  | 'rate-limited'
  | 'retention-expired'
  | 'idempotency-conflict';

export type SubmissionState =
  | { status: 'idle' }
  | { status: 'submitting'; intent: BookingIntent }
  | { status: 'confirmed'; intent: BookingIntent; booking: ConfirmedBooking }
  | { status: 'rejected'; intent: BookingIntent; reason: RejectionReason; retryAfterAt: number | null }
  | { status: 'unknown'; intent: BookingIntent; cause: UnknownCause; retryAfterAt: number | null };

type Outcome =
  | { status: 'rejected'; reason: RejectionReason; retryAfterAt: number | null }
  | { status: 'unknown'; cause: UnknownCause; retryAfterAt: number | null };

/** After these refusals the displayed wallet and catalog are stale and must be re-read. */
const REFRESH_AFTER: ReadonlySet<RejectionReason> = new Set([
  'price-changed',
  'insufficient-balance',
  'service-disabled',
  'no-operational-machine',
  'service-not-found',
]);

// Allowlist taken from the contract (CreateBookingEndpoint.cs): only these guarantee "nothing debited".
const DEFINITIVE_REFUSALS: Record<string, RejectionReason> = {
  VALIDATION_ERROR: 'invalid-request',
  INVALID_SERVICE_SELECTION: 'invalid-request',
  SERVICE_NOT_FOUND: 'service-not-found',
  WALLET_INSUFFICIENT_BALANCE: 'insufficient-balance',
  PRICE_CHANGED: 'price-changed',
  SERVICE_DISABLED: 'service-disabled',
  NO_OPERATIONAL_MACHINE: 'no-operational-machine',
  PAYLOAD_TOO_LARGE: 'request-too-large',
  UNSUPPORTED_MEDIA_TYPE: 'unsupported-media-type',
};

const unknown = (cause: UnknownCause, retryAfterAt: number | null = null): Outcome => ({ status: 'unknown', cause, retryAfterAt });
const rejected = (reason: RejectionReason, retryAfterAt: number | null = null): Outcome => ({ status: 'rejected', reason, retryAfterAt });

/**
 * Maps a failed attempt to what is financially known. `afterUnknown` is true when an earlier attempt of the
 * same intent already left the device: a refusal that says nothing about that attempt cannot resolve it.
 */
export function classifyFailure(error: unknown, afterUnknown = false): Outcome {
  // Thrown before any request leaves the device: certainly not a purchase.
  if (error instanceof ConfigError) return afterUnknown ? unknown('unclassified-error') : rejected('configuration');
  if (error instanceof ApiError) {
    if (error.errorCode === 'IDEMPOTENCY_CONFLICT') return unknown('idempotency-conflict');
    if (error.status === 429) {
      // The rate limiter answers before the purchase runs, so a first attempt is a certain refusal.
      return afterUnknown ? unknown('rate-limited', error.retryAfterAt) : rejected('rate-limited', error.retryAfterAt);
    }
    const refusal = error.errorCode ? DEFINITIVE_REFUSALS[error.errorCode] : undefined;
    if (refusal && error.status >= 400 && error.status < 500) return rejected(refusal);
    return unknown(error.status >= 500 ? 'server-error' : 'unclassified-error');
  }
  if (error instanceof TransportError) {
    if (error.kind === 'network') return unknown('network');
    if (error.kind === 'timeout') return unknown('timeout');
    return unknown('invalid-response');
  }
  if (error instanceof ZodError) return unknown('invalid-response');
  return unknown('unclassified-error');
}

type Options = {
  /** Test seam for the Idempotency-Key; production uses a random UUID v4. */
  generateKey?: () => string;
  now?: () => Date;
  /** How long the server remembers a key for replay; null while unproven (gate C2). */
  keyRetentionMs?: number | null;
};

const IDLE: SubmissionState = { status: 'idle' };

export function useBookingSubmission({
  generateKey = randomUUID,
  now = () => new Date(),
  keyRetentionMs = null,
}: Options = {}) {
  const queryClient = useQueryClient();
  // The ref is the synchronous guard: it changes before any await, so two taps in one tick cannot both pass.
  const stateRef = useRef<SubmissionState>(IDLE);
  const [state, setState] = useState<SubmissionState>(IDLE);

  const commit = useCallback((next: SubmissionState) => {
    stateRef.current = next;
    setState(next);
  }, []);

  const send = useCallback(
    async (intent: BookingIntent, afterUnknown: boolean) => {
      try {
        const booking = await createBooking({ idempotencyKey: intent.key, body: intent.body });
        commit({ status: 'confirmed', intent, booking });
      } catch (error) {
        const outcome = classifyFailure(error, afterUnknown);
        commit({ ...outcome, intent });
        if (outcome.status === 'rejected' && REFRESH_AFTER.has(outcome.reason)) {
          void queryClient.invalidateQueries({ queryKey: walletKey });
          void queryClient.invalidateQueries({ queryKey: catalogKey });
        }
      }
    },
    [commit, queryClient],
  );

  const blockedByRetryAfter = useCallback(
    (retryAfterAt: number | null) => retryAfterAt !== null && now().getTime() < retryAfterAt,
    [now],
  );

  /** Starts a new intent. Ignored while a purchase is in flight, its outcome is unknown, or Retry-After is pending. */
  const submit = useCallback(
    async (request: BookingRequest): Promise<void> => {
      const current = stateRef.current;
      if (current.status === 'submitting' || current.status === 'unknown') return;
      if (current.status === 'rejected' && blockedByRetryAfter(current.retryAfterAt)) return;

      const frozen: Readonly<BookingRequest> = Object.freeze({
        ...request,
        serviceCodes: Object.freeze([...request.serviceCodes]),
      });
      const intent: BookingIntent = {
        key: generateKey(),
        body: serializeBookingBody(frozen),
        request: frozen,
        createdAt: now().toISOString(),
      };
      commit({ status: 'submitting', intent });
      await send(intent, false);
    },
    [blockedByRetryAfter, commit, generateKey, now, send],
  );

  /**
   * Explicit user action: replays the same key and the same bytes of an unknown intent. Never automatic,
   * never after a conflict, an expired retention window, or while Retry-After is pending.
   */
  const verify = useCallback(async (): Promise<void> => {
    const current = stateRef.current;
    if (current.status !== 'unknown') return;
    if (current.cause === 'idempotency-conflict' || current.cause === 'retention-expired') return;
    if (blockedByRetryAfter(current.retryAfterAt)) return;
    if (keyRetentionMs !== null && now().getTime() - Date.parse(current.intent.createdAt) >= keyRetentionMs) {
      commit({ ...current, cause: 'retention-expired' });
      return;
    }
    commit({ status: 'submitting', intent: current.intent });
    await send(current.intent, true);
  }, [blockedByRetryAfter, commit, keyRetentionMs, now, send]);

  /** Leaves a settled result (confirmed or rejected) behind. An unknown outcome can never be dismissed. */
  const dismiss = useCallback(() => {
    const current = stateRef.current.status;
    if (current === 'confirmed' || current === 'rejected') commit(IDLE);
  }, [commit]);

  return { state, submit, verify, dismiss };
}
