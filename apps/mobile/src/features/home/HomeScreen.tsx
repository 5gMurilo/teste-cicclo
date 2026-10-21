import { useQuery } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { ActivityIndicator, FlatList, Pressable, StyleSheet, Text, View } from 'react-native';

import { CONFIG_ERROR_MESSAGE, isConfigError } from '../../shared/config/env';
import { formatBRL } from '../../shared/lib/money';
import { colors, spacing } from '../../shared/theme/tokens';
import { ServiceCard } from '../catalog/components/ServiceCard';
import { catalogOptions } from '../catalog/queries';
import { useCheckout } from '../checkout/CheckoutProvider';
import { blockedMessage } from '../checkout/messages';

export function HomeScreen() {
  const catalog = useQuery(catalogOptions());
  const checkout = useCheckout();
  const router = useRouter();
  const [blocked, setBlocked] = useState<string | null>(null);
  const { totalCents, availableCents, quantity } = checkout.derived;

  if (catalog.isPending) {
    return (
      <View style={styles.center} accessible accessibilityLabel="Carregando serviços">
        <ActivityIndicator />
        <Text style={styles.message}>Carregando serviços…</Text>
      </View>
    );
  }

  if (!catalog.data) {
    if (isConfigError(catalog.error)) {
      return (
        <View style={styles.center}>
          <Text accessibilityRole="alert" style={styles.error}>
            {CONFIG_ERROR_MESSAGE}
          </Text>
        </View>
      );
    }
    return (
      <View style={styles.center}>
        <Text style={styles.error}>Não foi possível carregar os serviços.</Text>
        <RetryButton onRetry={() => void catalog.refetch()} />
      </View>
    );
  }

  if (catalog.data.services.length === 0) {
    return (
      <View style={styles.center}>
        <Text style={styles.message}>Nenhum serviço disponível no momento.</Text>
      </View>
    );
  }

  return (
    <View style={styles.screen}>
      <FlatList
        data={catalog.data.services}
        extraData={checkout.selection}
        keyExtractor={(service) => service.code}
        renderItem={({ item }) => (
          <ServiceCard
            service={item}
            quantity={checkout.selection.quantities[item.code] ?? 0}
            onAdd={(code) => {
              const result = checkout.add(code);
              setBlocked(result.ok ? null : blockedMessage(result.reason));
            }}
            onRemove={(code) => {
              checkout.remove(code);
              setBlocked(null);
            }}
          />
        )}
        contentContainerStyle={styles.list}
        ListHeaderComponent={
          <View style={styles.header}>
            <Text accessibilityRole="header" style={styles.title}>
              Serviços disponíveis
            </Text>
            {catalog.isError ? (
              <View>
                <Text style={styles.error}>Preços e disponibilidade podem estar desatualizados.</Text>
                <RetryButton onRetry={() => void catalog.refetch()} />
              </View>
            ) : null}
          </View>
        }
      />
      <View style={styles.summary}>
        {blocked ? (
          <Text accessibilityRole="alert" accessibilityLiveRegion="polite" style={styles.error}>
            {blocked}
          </Text>
        ) : null}
        {totalCents !== null && availableCents !== null ? (
          <>
            <Text style={styles.summaryText}>{`Selecionado: ${formatBRL(totalCents)}`}</Text>
            <Text style={styles.summaryText}>{`Disponível para adicionar: ${formatBRL(availableCents)}`}</Text>
          </>
        ) : null}
        {quantity > 0 ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Revisar seleção"
            onPress={() => router.navigate('/checkout')}
            style={styles.review}
          >
            <Text style={styles.reviewText}>Revisar seleção</Text>
          </Pressable>
        ) : null}
      </View>
    </View>
  );
}

function RetryButton({ onRetry }: { onRetry: () => void }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel="Recarregar serviços"
      onPress={onRetry}
      style={styles.retry}
    >
      <Text style={styles.retryText}>Recarregar serviços</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1 },
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: spacing.sm, padding: spacing.md },
  list: { padding: spacing.md, gap: spacing.sm },
  header: { gap: spacing.sm, marginBottom: spacing.sm },
  title: { fontSize: 22, fontWeight: '600', color: colors.text },
  message: { fontSize: 16, color: colors.textMuted, textAlign: 'center' },
  error: { fontSize: 16, color: colors.danger, textAlign: 'center' },
  retry: { minHeight: 44, justifyContent: 'center', paddingHorizontal: spacing.md },
  retryText: { fontSize: 14, fontWeight: '600', color: colors.text, textDecorationLine: 'underline' },
  summary: {
    gap: spacing.xs,
    padding: spacing.md,
    borderTopColor: colors.border,
    borderTopWidth: 1,
    backgroundColor: colors.background,
  },
  summaryText: { fontSize: 16, color: colors.text },
  review: {
    minHeight: 44,
    alignItems: 'center',
    justifyContent: 'center',
    borderColor: colors.text,
    borderWidth: 1,
    borderRadius: 8,
  },
  reviewText: { fontSize: 16, fontWeight: '600', color: colors.text },
});
