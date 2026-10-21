import { randomUUID } from 'expo-crypto';
import { useCallback, useRef, useState } from 'react';
import { ZodError } from 'zod';

import { ApiError } from '../../shared/api/apiError';
import { TransportError } from '../../shared/api/client';
import { ConfigError } from '../../shared/config/env';
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
  | 'configuration';

/** Why the outcome of a sent purchase cannot be known from the response alone. */
export type UnknownCause =
  | 'network'
  | 'timeout'
  | 'server-error'
  | 'invalid-response'
  | 'unclassified-error'
  | 'idempotency-conflict';

export type SubmissionState =
  | { status: 'idle' }
  | { status: 'submitting'; intent: BookingIntent }
  | { status: 'confirmed'; intent: BookingIntent; booking: ConfirmedBooking }
  | { status: 'rejected'; intent: BookingIntent; reason: RejectionReason }
  | { status: 'unknown'; intent: BookingIntent; cause: UnknownCause };

type Outcome =
  | { status: 'rejected'; reason: RejectionReason }
  | { status: 'unknown'; cause: UnknownCause };

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

export function classifyFailure(error: unknown): Outcome {
  // Thrown before any request leaves the device: certainly not a purchase.
  if (error instanceof ConfigError) return { status: 'rejected', reason: 'configuration' };
  if (error instanceof ApiError) {
    if (error.errorCode === 'IDEMPOTENCY_CONFLICT') return { status: 'unknown', cause: 'idempotency-conflict' };
    const refusal = error.errorCode ? DEFINITIVE_REFUSALS[error.errorCode] : undefined;
    if (refusal && error.status >= 400 && error.status < 500) return { status: 'rejected', reason: refusal };
    return { status: 'unknown', cause: error.status >= 500 ? 'server-error' : 'unclassified-error' };
  }
  if (error instanceof TransportError) {
    if (error.kind === 'network') return { status: 'unknown', cause: 'network' };
    if (error.kind === 'timeout') return { status: 'unknown', cause: 'timeout' };
    return { status: 'unknown', cause: 'invalid-response' };
  }
  if (error instanceof ZodError) return { status: 'unknown', cause: 'invalid-response' };
  return { status: 'unknown', cause: 'unclassified-error' };
}

type Options = {
  /** Test seam for the Idempotency-Key; production uses a random UUID v4. */
  generateKey?: () => string;
  now?: () => Date;
};

const IDLE: SubmissionState = { status: 'idle' };

export function useBookingSubmission({ generateKey = randomUUID, now = () => new Date() }: Options = {}) {
  // The ref is the synchronous guard: it changes before any await, so two taps in one tick cannot both pass.
  const stateRef = useRef<SubmissionState>(IDLE);
  const [state, setState] = useState<SubmissionState>(IDLE);

  const commit = useCallback((next: SubmissionState) => {
    stateRef.current = next;
    setState(next);
  }, []);

  const send = useCallback(
    async (intent: BookingIntent) => {
      try {
        const booking = await createBooking({ idempotencyKey: intent.key, body: intent.body });
        commit({ status: 'confirmed', intent, booking });
      } catch (error) {
        commit({ ...classifyFailure(error), intent });
      }
    },
    [commit],
  );

  /** Starts a new intent. Ignored while a purchase is in flight or its outcome is still unknown. */
  const submit = useCallback(
    async (request: BookingRequest): Promise<void> => {
      const current = stateRef.current.status;
      if (current === 'submitting' || current === 'unknown') return;

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
      await send(intent);
    },
    [commit, generateKey, now, send],
  );

  /** Leaves a settled result (confirmed or rejected) behind. An unknown outcome can never be dismissed. */
  const dismiss = useCallback(() => {
    const current = stateRef.current.status;
    if (current === 'confirmed' || current === 'rejected') commit(IDLE);
  }, [commit]);

  return { state, submit, dismiss };
}
