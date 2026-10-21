import { useEffect, useRef, useState } from 'react';
import { AppState, StyleSheet, Text, View } from 'react-native';

import { colors } from '../../../shared/theme/tokens';

const timeFormat = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

/** Seconds left until the server deadline; the local clock only informs, the server decides on confirm. */
export function secondsUntil(expiresAt: string, nowMs: number): number {
  return Math.max(0, Math.ceil((Date.parse(expiresAt) - nowMs) / 1000));
}

type Props = {
  /** Deadline from the API (UTC). */
  expiresAt: string;
  /** Called once per `expiresAt` when the count reaches zero, so the caller can re-read the real state. */
  onExpired: () => void;
};

export function OfferCountdown({ expiresAt, onExpired }: Props) {
  const [now, setNow] = useState(() => Date.now());
  const remaining = secondsUntil(expiresAt, now);
  const expired = remaining === 0;

  const onExpiredRef = useRef(onExpired);
  const firedFor = useRef<string | null>(null);
  useEffect(() => {
    onExpiredRef.current = onExpired;
  });

  // The interval only drives rendering; the value is always recomputed from timestamps.
  useEffect(() => {
    if (expired) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [expired]);

  // Returning to the foreground recalculates at once instead of resuming a stale count.
  useEffect(() => {
    const subscription = AppState.addEventListener('change', (state) => {
      if (state === 'active') setNow(Date.now());
    });
    return () => subscription.remove();
  }, []);

  useEffect(() => {
    if (expired && firedFor.current !== expiresAt) {
      firedFor.current = expiresAt;
      onExpiredRef.current();
    }
  }, [expired, expiresAt]);

  // The spoken label is fixed (deadline time, then "vencida") so the reader is not interrupted every second.
  const label = expired
    ? 'Oferta de máquina vencida'
    : `Oferta de máquina válida até ${timeFormat.format(new Date(expiresAt))}`;

  return (
    <View accessible accessibilityRole="timer" accessibilityLabel={label}>
      <Text style={expired ? styles.expired : styles.remaining}>
        {expired ? 'Oferta vencida' : `Tempo restante: ${remaining} s`}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  remaining: { fontSize: 14, fontWeight: '600', color: colors.text },
  expired: { fontSize: 14, fontWeight: '600', color: colors.danger },
});
