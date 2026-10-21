import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { ActivityIndicator, FlatList, Pressable, StyleSheet, Text, View } from 'react-native';

import { ApiError } from '../../shared/api/apiError';
import { CONFIG_ERROR_MESSAGE, isConfigError } from '../../shared/config/env';
import { formatBRL } from '../../shared/lib/money';
import { colors, spacing } from '../../shared/theme/tokens';
import { OfferCountdown } from './components/OfferCountdown';
import { bookingsListOptions } from './listQuery';
import { bookingsKey } from './queryKeys';
import type { Booking } from './schema';
import { bookingPhase, statusLabel } from './status';
import { LIST_POLL_MS, useBookingPolling } from './useBookingPolling';

type Filter = 'active' | 'history';

const FILTERS: { value: Filter; label: string }[] = [
  { value: 'active', label: 'Em andamento' },
  { value: 'history', label: 'Histórico' },
];

const EMPTY_TEXT: Record<Filter, string> = {
  active: 'Nenhuma contratação em andamento.',
  history: 'Nenhuma contratação no histórico.',
};

const dateFormat = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short' });
const timeFormat = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const shortId = (id: string) => id.slice(0, 8);

const hasLiveBookings = (data: { pages: { items: Booking[] }[] } | undefined) =>
  data?.pages.some((page) => page.items.some((booking) => bookingPhase(booking.items) !== 'history')) ?? false;

export function BookingsScreen() {
  const refetchInterval = useBookingPolling(LIST_POLL_MS, hasLiveBookings);
  const list = useInfiniteQuery({ ...bookingsListOptions(), refetchInterval });
  const router = useRouter();
  const [filter, setFilter] = useState<Filter>('active');

  if (list.isPending) {
    return (
      <View style={styles.center} accessible accessibilityLabel="Carregando contratações">
        <ActivityIndicator />
        <Text style={styles.message}>Carregando contratações…</Text>
      </View>
    );
  }

  if (!list.data) {
    if (isConfigError(list.error)) {
      return (
        <View style={styles.center}>
          <Text accessibilityRole="alert" style={styles.error}>
            {CONFIG_ERROR_MESSAGE}
          </Text>
        </View>
      );
    }
    const unavailable = list.error instanceof ApiError && list.error.status === 404;
    return (
      <View style={styles.center}>
        <Text style={styles.error}>
          {unavailable
            ? 'A listagem de contratações não está disponível nesta versão da API.'
            : 'Não foi possível carregar suas contratações.'}
        </Text>
        <Action label="Tentar novamente" onPress={() => void list.refetch()} />
      </View>
    );
  }

  const visible = list.data.bookings.filter((b) => bookingPhase(b.items) === filter);

  return (
    <View style={styles.screen}>
      <FlatList
        data={visible}
        keyExtractor={(booking) => booking.bookingId}
        renderItem={({ item }) => (
          <BookingCard booking={item} onOpen={() => router.push(`/bookings/${item.bookingId}`)} />
        )}
        contentContainerStyle={styles.list}
        ListHeaderComponent={
          <View style={styles.header}>
            <Text accessibilityRole="header" style={styles.title}>
              Serviços
            </Text>
            <View accessibilityRole="radiogroup" style={styles.filters}>
              {FILTERS.map(({ value, label }) => (
                <Pressable
                  key={value}
                  accessibilityRole="radio"
                  accessibilityLabel={label}
                  accessibilityState={{ selected: filter === value, checked: filter === value }}
                  onPress={() => setFilter(value)}
                  style={[styles.filter, filter === value && styles.filterSelected]}
                >
                  <Text style={styles.filterText}>{label}</Text>
                </Pressable>
              ))}
            </View>
            {list.isError ? (
              <Text style={styles.error}>Não foi possível atualizar. Os dados abaixo podem estar desatualizados.</Text>
            ) : null}
          </View>
        }
        ListEmptyComponent={
          <View style={styles.empty}>
            <Text style={styles.message}>{EMPTY_TEXT[filter]}</Text>
            <Action label="Escolher serviços" onPress={() => router.navigate('/')} />
          </View>
        }
        ListFooterComponent={
          list.data.hasMore ? (
            <View style={styles.footer}>
              <Text style={styles.message}>Mostrando as contratações carregadas. Há mais registros.</Text>
              <Action
                label={list.isFetchingNextPage ? 'Carregando…' : 'Carregar mais'}
                onPress={() => void list.fetchNextPage()}
              />
            </View>
          ) : undefined
        }
      />
    </View>
  );
}

function BookingCard({ booking, onOpen }: { booking: Booking; onOpen: () => void }) {
  const queryClient = useQueryClient();
  const created = dateFormat.format(new Date(booking.createdAt));
  const offers = booking.items.filter((item) => item.status === 'to_be_confirmed' && item.expiresAt !== null);
  const offerSummary = offers.length === 0 ? '' : `, oferta de máquina aguardando confirmação até ${timeFormat.format(new Date(offers[0].expiresAt as string))}`;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`Abrir contratação ${shortId(booking.bookingId)}, ${created}, ${formatBRL(booking.chargedAmountCents)}${offerSummary}`}
      onPress={onOpen}
      style={[styles.card, offers.length > 0 && styles.cardOffer]}
    >
      <Text style={styles.name}>{`Contratação ${shortId(booking.bookingId)}`}</Text>
      <Text style={styles.detail}>{`${created} · ${formatBRL(booking.chargedAmountCents)}`}</Text>
      {booking.items.map((item) => (
        <Text key={item.itemId} style={styles.detail}>
          {`${item.serviceCode} · ${formatBRL(item.unitPriceCents)} · ${statusLabel(item.status)}`}
        </Text>
      ))}
      {offers.map((item) => (
        <View key={`offer-${item.itemId}`} style={styles.offer}>
          <Text style={styles.offerTitle}>{`Oferta de máquina para ${item.serviceCode}: confirme no detalhe`}</Text>
          <OfferCountdown
            expiresAt={item.expiresAt as string}
            onExpired={() => void queryClient.invalidateQueries({ queryKey: bookingsKey })}
          />
        </View>
      ))}
    </Pressable>
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
  screen: { flex: 1 },
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: spacing.sm, padding: spacing.md },
  list: { padding: spacing.md, gap: spacing.sm },
  header: { gap: spacing.sm, marginBottom: spacing.sm },
  title: { fontSize: 22, fontWeight: '600', color: colors.text },
  filters: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  filter: {
    minHeight: 44,
    justifyContent: 'center',
    paddingHorizontal: spacing.md,
    borderColor: colors.border,
    borderWidth: 1,
    borderRadius: 8,
  },
  filterSelected: { borderColor: colors.text, borderWidth: 2 },
  filterText: { fontSize: 14, fontWeight: '600', color: colors.text },
  empty: { alignItems: 'center', gap: spacing.sm, padding: spacing.md },
  footer: { alignItems: 'center', gap: spacing.sm, padding: spacing.md },
  card: { gap: spacing.xs, padding: spacing.md, borderColor: colors.border, borderWidth: 1, borderRadius: 8 },
  cardOffer: { borderColor: colors.text, borderWidth: 2 },
  offer: { gap: spacing.xs, marginTop: spacing.xs },
  offerTitle: { fontSize: 14, fontWeight: '700', color: colors.text },
  name: { fontSize: 16, fontWeight: '600', color: colors.text },
  detail: { fontSize: 14, color: colors.text },
  message: { fontSize: 16, color: colors.textMuted, textAlign: 'center' },
  error: { fontSize: 16, color: colors.danger, textAlign: 'center' },
  button: {
    minHeight: 44,
    justifyContent: 'center',
    paddingHorizontal: spacing.md,
    borderColor: colors.text,
    borderWidth: 1,
    borderRadius: 8,
  },
  buttonText: { fontSize: 14, fontWeight: '600', color: colors.text },
});
