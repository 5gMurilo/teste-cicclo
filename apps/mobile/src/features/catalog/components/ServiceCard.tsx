import { Pressable, StyleSheet, Text, View } from 'react-native';

import { formatBRL } from '../../../shared/lib/money';
import { colors, spacing } from '../../../shared/theme/tokens';
import type { Service } from '../queries';

type Props = {
  service: Service;
  /** Units currently selected; shown only together with `onRemove`. */
  quantity?: number;
  /** Without it the card is read-only: no action is rendered. */
  onAdd?: (code: string) => void;
  onRemove?: (code: string) => void;
};

const REASON_TEXT: Record<string, string> = {
  SERVICE_DISABLED: 'Serviço desativado',
  NO_OPERATIONAL_MACHINE: 'Sem máquina operacional no momento',
};
const UNKNOWN_REASON_TEXT = 'Indisponível no momento';

function availabilityText(service: Service): string {
  if (service.canBeBooked) return 'Disponível';
  const reason = service.unavailabilityReason;
  return (reason && REASON_TEXT[reason]) || UNKNOWN_REASON_TEXT;
}

export function ServiceCard({ service, quantity = 0, onAdd, onRemove }: Props) {
  const price = formatBRL(service.unitPriceCents);
  const status = availabilityText(service);
  const subject = `${service.name}, ${price}`;

  return (
    <View style={styles.card}>
      <View style={styles.info} accessible accessibilityLabel={`${subject}. ${status}`}>
        <Text style={styles.name}>{service.name}</Text>
        <Text style={styles.price}>{price}</Text>
        <Text style={service.canBeBooked ? styles.ok : styles.unavailable}>{status}</Text>
      </View>
      {onAdd ? (
        <View style={styles.actions}>
          {onRemove && quantity > 0 ? (
            <>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={`Remover ${subject}`}
                onPress={() => onRemove(service.code)}
                style={styles.button}
              >
                <Text style={styles.buttonText}>Remover</Text>
              </Pressable>
              <Text accessibilityLabel={`Quantidade: ${quantity}`}>{quantity}</Text>
            </>
          ) : null}
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={`Adicionar ${subject}`}
            accessibilityState={{ disabled: !service.canBeBooked }}
            disabled={!service.canBeBooked}
            onPress={() => onAdd(service.code)}
            style={[styles.button, !service.canBeBooked && styles.buttonDisabled]}
          >
            <Text style={styles.buttonText}>Adicionar</Text>
          </Pressable>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: spacing.sm,
    padding: spacing.md,
    borderColor: colors.border,
    borderWidth: 1,
    borderRadius: 8,
  },
  info: { flexShrink: 1, gap: spacing.xs },
  name: { fontSize: 18, fontWeight: '600', color: colors.text },
  price: { fontSize: 16, color: colors.text },
  ok: { fontSize: 14, color: colors.textMuted },
  unavailable: { fontSize: 14, color: colors.danger },
  actions: { flexDirection: 'row', alignItems: 'center', gap: spacing.sm },
  button: {
    minHeight: 44,
    justifyContent: 'center',
    paddingHorizontal: spacing.md,
    borderColor: colors.text,
    borderWidth: 1,
    borderRadius: 8,
  },
  buttonDisabled: { opacity: 0.4 },
  buttonText: { fontSize: 14, fontWeight: '600', color: colors.text },
});
