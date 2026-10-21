import { useQuery } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { ApiError } from '../../shared/api/apiError';
import { CONFIG_ERROR_MESSAGE, isConfigError } from '../../shared/config/env';
import { formatBRL } from '../../shared/lib/money';
import { colors, spacing } from '../../shared/theme/tokens';
import { bookingDetailOptions, isBookingId } from './detailQuery';
import type { BookingItem } from './schema';
import { statusLabel } from './status';

const dateFormat = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short' });
const timeFormat = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const shortId = (id: string) => id.slice(0, 8);

export function BookingDetailsScreen({ bookingId }: { bookingId: string | undefined }) {
  const router = useRouter();
  const valid = bookingId !== undefined && isBookingId(bookingId);
  const detail = useQuery(bookingDetailOptions(valid ? bookingId : ''));
  const back = <Action label="Voltar para Serviços" onPress={() => router.navigate('/services')} />;

  let body;
  if (!valid) {
    body = <Problem message="Identificador de contratação inválido." />;
  } else if (detail.isPending) {
    body = (
      <View style={styles.center} accessible accessibilityLabel="Carregando contratação">
        <ActivityIndicator />
        <Text style={styles.message}>Carregando contratação…</Text>
      </View>
    );
  } else if (!detail.data) {
    const notFound = detail.error instanceof ApiError && detail.error.status === 404;
    body = isConfigError(detail.error) ? (
      <Problem message={CONFIG_ERROR_MESSAGE} />
    ) : (
      <Problem
        message={notFound ? 'Contratação não encontrada.' : 'Não foi possível carregar a contratação.'}
        retry={notFound ? undefined : () => void detail.refetch()}
      />
    );
  } else {
    const booking = detail.data;
    body = (
      <ScrollView contentContainerStyle={styles.content}>
        <Text accessibilityRole="header" style={styles.title}>{`Contratação ${shortId(booking.bookingId)}`}</Text>
        <Text style={styles.detail}>{`Contratada em ${dateFormat.format(new Date(booking.createdAt))}`}</Text>
        <Text style={styles.detail}>{`Valor cobrado: ${formatBRL(booking.chargedAmountCents)}`}</Text>
        {detail.isError ? (
          <Text style={styles.error}>Não foi possível atualizar. Os dados abaixo podem estar desatualizados.</Text>
        ) : null}
        {booking.items.map((item) => (
          <ItemRow key={item.itemId} item={item} />
        ))}
        <Text style={styles.note}>As máquinas são simuladas.</Text>
      </ScrollView>
    );
  }

  return (
    <SafeAreaView edges={['top']} style={styles.screen}>
      {body}
      <View style={styles.footer}>{back}</View>
    </SafeAreaView>
  );
}

function ItemRow({ item }: { item: BookingItem }) {
  return (
    <View
      style={styles.item}
      accessible
      accessibilityLabel={`${item.serviceCode}, ${formatBRL(item.unitPriceCents)}, ${statusLabel(item.status)}`}
    >
      <Text style={styles.name}>{`${item.serviceCode} · ${formatBRL(item.unitPriceCents)}`}</Text>
      <Text style={styles.detail}>{statusLabel(item.status)}</Text>
      {item.status === 'queued' && item.queuePosition !== null ? (
        <Text style={styles.detail}>{`Posição na fila: ${item.queuePosition}`}</Text>
      ) : null}
      {item.status === 'to_be_confirmed' && item.expiresAt !== null ? (
        <Text style={styles.detail}>{`Oferta válida até ${timeFormat.format(new Date(item.expiresAt))}`}</Text>
      ) : null}
      {item.machineId !== null && (item.status === 'to_be_confirmed' || item.status === 'in_progress') ? (
        <Text style={styles.detail}>{`Máquina simulada ${shortId(item.machineId)}`}</Text>
      ) : null}
    </View>
  );
}

function Problem({ message, retry }: { message: string; retry?: () => void }) {
  return (
    <View style={styles.center}>
      <Text accessibilityRole="alert" style={styles.error}>
        {message}
      </Text>
      {retry ? <Action label="Tentar novamente" onPress={retry} /> : null}
    </View>
  );
}

function Action({ label, onPress }: { label: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={label} onPress={onPress} style={styles.button}>
      <Text style={styles.buttonText}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: colors.background },
  content: { padding: spacing.md, gap: spacing.sm },
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: spacing.sm, padding: spacing.md },
  footer: { padding: spacing.md, borderTopColor: colors.border, borderTopWidth: 1 },
  title: { fontSize: 22, fontWeight: '600', color: colors.text },
  item: { gap: spacing.xs, padding: spacing.md, borderColor: colors.border, borderWidth: 1, borderRadius: 8 },
  name: { fontSize: 16, fontWeight: '600', color: colors.text },
  detail: { fontSize: 14, color: colors.text },
  note: { fontSize: 14, color: colors.textMuted },
  message: { fontSize: 16, color: colors.textMuted, textAlign: 'center' },
  error: { fontSize: 16, color: colors.danger, textAlign: 'center' },
  button: {
    minHeight: 44,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: spacing.md,
    borderColor: colors.text,
    borderWidth: 1,
    borderRadius: 8,
  },
  buttonText: { fontSize: 14, fontWeight: '600', color: colors.text },
});
