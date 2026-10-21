import { Pressable, StyleSheet, Text, View } from 'react-native';

import { colors, spacing } from '../../shared/theme/tokens';
import { canActivate, useActivateItem } from './activateItem';
import { commandErrorMessage, type CommandState } from './itemCommand';
import type { BookingItem } from './schema';

/** The commands an item currently offers. Hidden entirely when the API capability is not proven. */
export function ItemActions({ bookingId, item }: { bookingId: string; item: BookingItem }) {
  const activate = useActivateItem(bookingId);

  return (
    <View style={styles.actions}>
      {canActivate(item.status) ? (
        <ActionButton
          label={`Ativar ${item.serviceCode}`}
          busy={isPendingFor(activate.state, item.itemId)}
          disabled={activate.state.status === 'pending'}
          onPress={() => void activate.run(item.itemId)}
        />
      ) : null}
      <Failure state={activate.state} itemId={item.itemId} />
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
});
