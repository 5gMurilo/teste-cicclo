import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useRef, useState } from 'react';

import { ApiError } from '../../shared/api/apiError';
import { requestJson } from '../../shared/api/client';
import { bookingsKey } from './queryKeys';
import { isBookingId, InvalidBookingIdError } from './detailQuery';
import { bookingItemSchema, type BookingItem } from './schema';

export type ItemAction = 'activate' | 'confirm' | 'cancel' | 'report-issue';

/** POST /bookings/{bookingId}/items/{itemId}/{action}. Ids are validated before anything is sent. */
export async function postItemCommand(input: {
  bookingId: string;
  itemId: string;
  action: ItemAction;
  body?: string;
}): Promise<unknown> {
  if (!isBookingId(input.bookingId) || !isBookingId(input.itemId)) throw new InvalidBookingIdError();
  return requestJson({
    method: 'POST',
    path: `/bookings/${input.bookingId}/items/${input.itemId}/${input.action}`,
    body: input.body,
  });
}

/** Validates the item a command answers with (BookingItemResponse); a bad body is an error, never a guess. */
export function parseItemResponse(json: unknown): BookingItem {
  return bookingItemSchema.parse(json);
}

export type CommandState<T> =
  | { status: 'idle' }
  | { status: 'pending'; itemId: string }
  | { status: 'done'; itemId: string; result: T }
  | { status: 'failed'; itemId: string; error: unknown };

/**
 * Runs one item command at a time. No retry and no automatic replay: the user presses again if they want.
 * Whatever the outcome, the bookings cache is refreshed so the screen shows the server's real state.
 */
export function useItemCommand<T>(send: (itemId: string) => Promise<T>) {
  const queryClient = useQueryClient();
  const busy = useRef(false);
  const [state, setState] = useState<CommandState<T>>({ status: 'idle' });

  const run = useCallback(
    async (itemId: string): Promise<void> => {
      if (busy.current) return;
      busy.current = true;
      setState({ status: 'pending', itemId });
      try {
        const result = await send(itemId);
        setState({ status: 'done', itemId, result });
      } catch (error) {
        setState({ status: 'failed', itemId, error });
      } finally {
        busy.current = false;
        await queryClient.invalidateQueries({ queryKey: bookingsKey });
      }
    },
    [queryClient, send],
  );

  return { state, run };
}

const timeFormat = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

/** User-facing text for a failed item command, by status and errorCode only. */
export function commandErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.errorCode === 'MACHINE_OFFER_EXPIRED' || error.status === 410) {
      return 'A oferta venceu e a execução voltou para a fila. Veja o estado atualizado abaixo.';
    }
    if (error.errorCode === 'INVALID_STATE_TRANSITION') {
      return 'O estado da execução mudou e a ação não se aplica mais. Veja o estado atualizado abaixo.';
    }
    if (error.status === 404) return 'Contratação ou execução não encontrada.';
    if (error.status === 429) {
      return error.retryAfterAt === null
        ? 'Muitas tentativas. Aguarde um instante e tente de novo.'
        : `Muitas tentativas. Aguarde até ${timeFormat.format(new Date(error.retryAfterAt))} e tente de novo.`;
    }
  }
  return 'Não foi possível concluir a ação. Veja o estado atualizado abaixo e tente de novo.';
}
