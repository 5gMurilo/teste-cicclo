import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { formatBRL } from '../../shared/lib/money';
import { colors, spacing } from '../../shared/theme/tokens';
import { canActivate, useActivateItem } from './activateItem';
import { canCancel, useCancelItem } from './cancelItem';
import { OfferCountdown } from './components/OfferCountdown';
import { confirmEnabled, useConfirmItem } from './confirmItem';
import { commandErrorMessage, type CommandState } from './itemCommand';
import { bookingsKey } from './queryKeys';
import type { BookingItem } from './schema';

/** The commands an item currently offers. Hidden entirely when the API capability is not proven. */
export function ItemActions({ bookingId, item }: { bookingId: string; item: BookingItem }) {
  const queryClient = useQueryClient();
  const activate = useActivateItem(bookingId);
  const confirm = useConfirmItem(bookingId);
  const cancel = useCancelItem(bookingId);
  const [askingCancel, setAskingCancel] = useState(false);
  // The deadline we already saw expire: from then on the offer cannot be confirmed from this screen.
  const [expiredDeadline, setExpiredDeadline] = useState<string | null>(null);
  const offerExpired = item.expiresAt !== null && expiredDeadline === item.expiresAt;
  const markExpired = () => {
    setExpiredDeadline(item.expiresAt);
    void queryClient.invalidateQueries({ queryKey: bookingsKey });
  };

  return (
    <View style={styles.actions}>
      {item.status === 'to_be_confirmed' && item.expiresAt !== null ? (
        <OfferCountdown
          expiresAt={item.expiresAt}
          onExpired={markExpired}
        />
      ) : null}
      {confirmEnabled() && item.status === 'to_be_confirmed' && item.expiresAt !== null && !offerExpired ? (
        <ActionButton
          label={`Confirmar ${item.serviceCode}`}
          busy={isPendingFor(confirm.state, item.itemId)}
          disabled={confirm.state.status === 'pending'}
          onPress={() => void confirm.confirm(item).then((sent) => !sent && markExpired())}
        />
      ) : null}
      {canActivate(item.status) ? (
        <ActionButton
          label={`Ativar ${item.serviceCode}`}
          busy={isPendingFor(activate.state, item.itemId)}
          disabled={activate.state.status === 'pending'}
          onPress={() => void activate.run(item.itemId)}
        />
      ) : null}
      {canCancel(item.status) ? (
        askingCancel ? (
          <View style={styles.actions}>
            <Text style={styles.note}>
              Cancelar esta execução? O estorno só é mostrado depois da confirmação do servidor.
            </Text>
            <ActionButton
              label={`Confirmar cancelamento de ${item.serviceCode}`}
              busy={isPendingFor(cancel.state, item.itemId)}
              disabled={cancel.state.status === 'pending'}
              onPress={() => {
                void cancel.run(item.itemId);
                setAskingCancel(false);
              }}
            />
            <ActionButton label={`Manter ${item.serviceCode}`} onPress={() => setAskingCancel(false)} />
          </View>
        ) : (
          <ActionButton
            label={`Cancelar ${item.serviceCode}`}
            disabled={cancel.state.status === 'pending'}
            onPress={() => setAskingCancel(true)}
          />
        )
      ) : null}
      {cancel.state.status === 'done' && cancel.state.itemId === item.itemId ? (
        <Text accessibilityRole="alert" accessibilityLiveRegion="polite" style={styles.note}>
          {cancel.state.result.refundedAmountCents > 0
            ? `Execução cancelada. Estorno confirmado: ${formatBRL(cancel.state.result.refundedAmountCents)}.`
            : 'Execução cancelada.'}
        </Text>
      ) : null}
      <Failure state={cancel.state} itemId={item.itemId} />
      <Failure state={activate.state} itemId={item.itemId} />
      <Failure state={confirm.state} itemId={item.itemId} />
    </View>
  );
}

function isPendingFor(state: CommandState<unknown>, itemId: string): boolean {
  return state.status === 'pending' && state.itemId === itemId;
}

function Failure({ state, itemId }: { state: CommandState<unknown>; itemId: string }) {
  if (state.status !== 'failed' || state.itemId !== itemId) return null;
  return (
    <Text accessibilityRole="alert" accessibilityLiveRegion="polite" style={styles.error}>
      {commandErrorMessage(state.error)}
    </Text>
  );
}

export function ActionButton(props: { label: string; busy?: boolean; disabled?: boolean; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={props.label}
      accessibilityState={{ disabled: props.disabled ?? false, busy: props.busy ?? false }}
      disabled={props.disabled}
      onPress={props.onPress}
      style={[styles.button, props.disabled && styles.disabled]}
    >
      <Text style={styles.buttonText}>{props.busy ? 'Aguarde…' : props.label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  actions: { gap: spacing.xs },
  button: {
    minHeight: 44,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: spacing.md,
    borderColor: colors.text,
    borderWidth: 1,
    borderRadius: 8,
  },
  disabled: { opacity: 0.5 },
  buttonText: { fontSize: 14, fontWeight: '600', color: colors.text },
  error: { fontSize: 14, color: colors.danger },
  note: { fontSize: 14, color: colors.text },
});
