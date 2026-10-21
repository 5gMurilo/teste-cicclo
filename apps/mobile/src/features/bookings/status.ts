// Item statuses of the bookings contract (BookingItemStatusContract in apps/api).
const NON_TERMINAL = ['pending_activation', 'queued', 'to_be_confirmed', 'in_progress'] as const;
const TERMINAL = ['completed', 'cancelled', 'failed'] as const;

export type KnownItemStatus = (typeof NON_TERMINAL)[number] | (typeof TERMINAL)[number];

const LABELS: Record<KnownItemStatus, string> = {
  pending_activation: 'Paga, aguardando ativação',
  queued: 'Na fila',
  to_be_confirmed: 'Máquina ofertada, aguardando confirmação',
  in_progress: 'Em execução',
  completed: 'Concluído',
  cancelled: 'Cancelado',
  failed: 'Falhou',
};

export const UNRECOGNIZED_STATUS_LABEL = 'Estado não reconhecido';

export function isKnownStatus(status: string): status is KnownItemStatus {
  return (NON_TERMINAL as readonly string[]).includes(status) || (TERMINAL as readonly string[]).includes(status);
}

/** Only a known terminal status ends an item; an unknown one is never assumed to be finished. */
export function isTerminalStatus(status: string): boolean {
  return (TERMINAL as readonly string[]).includes(status);
}

export function statusLabel(status: string): string {
  return isKnownStatus(status) ? LABELS[status] : UNRECOGNIZED_STATUS_LABEL;
}

export type BookingPhase = 'active' | 'history' | 'empty';

/**
 * A booking is history only when every item is terminal. One non-terminal (or unrecognized) item keeps
 * the whole batch active, so a mixed batch never loses its finished items from the summary.
 */
export function bookingPhase(items: readonly { status: string }[]): BookingPhase {
  if (items.length === 0) return 'empty';
  return items.every((item) => isTerminalStatus(item.status)) ? 'history' : 'active';
}

export function hasUnrecognizedStatus(items: readonly { status: string }[]): boolean {
  return items.some((item) => !isKnownStatus(item.status));
}
