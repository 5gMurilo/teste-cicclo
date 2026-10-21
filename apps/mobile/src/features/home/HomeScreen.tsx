import { useQuery } from '@tanstack/react-query';
import { ActivityIndicator, FlatList, Pressable, StyleSheet, Text, View } from 'react-native';

import { colors, spacing } from '../../shared/theme/tokens';
import { ServiceCard } from '../catalog/components/ServiceCard';
import { catalogOptions } from '../catalog/queries';

export function HomeScreen() {
  const catalog = useQuery(catalogOptions());

  if (catalog.isPending) {
    return (
      <View style={styles.center} accessible accessibilityLabel="Carregando serviços">
        <ActivityIndicator />
        <Text style={styles.message}>Carregando serviços…</Text>
      </View>
    );
  }

  if (!catalog.data) {
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
    <FlatList
      data={catalog.data.services}
      keyExtractor={(service) => service.code}
      renderItem={({ item }) => <ServiceCard service={item} />}
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
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: spacing.sm, padding: spacing.md },
  list: { padding: spacing.md, gap: spacing.sm },
  header: { gap: spacing.sm, marginBottom: spacing.sm },
  title: { fontSize: 22, fontWeight: '600', color: colors.text },
  message: { fontSize: 16, color: colors.textMuted, textAlign: 'center' },
  error: { fontSize: 16, color: colors.danger, textAlign: 'center' },
  retry: { minHeight: 44, justifyContent: 'center', paddingHorizontal: spacing.md },
  retryText: { fontSize: 14, fontWeight: '600', color: colors.text, textDecorationLine: 'underline' },
});
