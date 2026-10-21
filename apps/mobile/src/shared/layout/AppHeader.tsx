import { Pressable, StyleSheet, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { formatBRL } from '../lib/money';
import { colors, spacing } from '../theme/tokens';

export type BalanceState =
  | { status: 'loading' }
  | { status: 'ready'; cents: number }
  /** A previously confirmed value kept after a failed refresh; shown labelled, never as current. */
  | { status: 'stale'; cents: number; updatedAtLabel: string }
  | { status: 'error' };

type Props = {
  displayName: string;
  balance: BalanceState;
  onRetry: () => void;
};

// Owns the top safe-area inset for every tab.
export function AppHeader({ displayName, balance, onRetry }: Props) {
  return (
    <SafeAreaView edges={['top']} style={styles.container}>
      <View style={styles.row}>
        <Text accessibilityRole="header" numberOfLines={2} ellipsizeMode="tail" style={styles.greeting}>
          {`Olá! ${displayName}`}
        </Text>
        <Balance balance={balance} onRetry={onRetry} />
      </View>
    </SafeAreaView>
  );
}

function Balance({ balance, onRetry }: { balance: BalanceState; onRetry: () => void }) {
  switch (balance.status) {
    case 'loading':
      return (
        <View style={styles.balance} accessible accessibilityLabel="Carregando saldo">
          <Text style={styles.muted}>Carregando saldo…</Text>
        </View>
      );
    case 'ready':
      return (
        <View style={styles.balance} accessible accessibilityLabel={`Saldo da carteira: ${formatBRL(balance.cents)}`}>
          <Text style={styles.muted}>Saldo da carteira</Text>
          <Text style={styles.amount}>{formatBRL(balance.cents)}</Text>
        </View>
      );
    case 'stale':
      return (
        <View style={styles.balance}>
          <View
            accessible
            accessibilityLabel={`Saldo desatualizado: ${formatBRL(balance.cents)}, ${balance.updatedAtLabel}`}
          >
            <Text style={styles.warning}>Desatualizado · {balance.updatedAtLabel}</Text>
            <Text style={styles.amount}>{formatBRL(balance.cents)}</Text>
          </View>
          <RetryButton label="Atualizar saldo" onRetry={onRetry} />
        </View>
      );
    case 'error':
      return (
        <View style={styles.balance}>
          <Text style={styles.danger}>Saldo indisponível</Text>
          <RetryButton label="Tentar novamente" onRetry={onRetry} />
        </View>
      );
  }
}

function RetryButton({ label, onRetry }: { label: string; onRetry: () => void }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={label} onPress={onRetry} style={styles.retry}>
      <Text style={styles.retryText}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  container: { backgroundColor: colors.background, borderBottomColor: colors.border, borderBottomWidth: 1 },
  row: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: spacing.sm,
    padding: spacing.md,
  },
  greeting: { flexShrink: 1, fontSize: 18, fontWeight: '600', color: colors.text },
  balance: { alignItems: 'flex-end', gap: spacing.xs, marginLeft: 'auto' },
  muted: { fontSize: 13, color: colors.textMuted },
  amount: { fontSize: 18, fontWeight: '700', color: colors.text },
  warning: { fontSize: 13, color: colors.warning },
  danger: { fontSize: 14, color: colors.danger },
  retry: { minHeight: 44, justifyContent: 'center', paddingHorizontal: spacing.sm },
  retryText: { fontSize: 14, fontWeight: '600', color: colors.text, textDecorationLine: 'underline' },
});
