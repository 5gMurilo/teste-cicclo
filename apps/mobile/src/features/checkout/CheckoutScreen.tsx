import { useRouter } from 'expo-router';
import { useEffect, useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';

import { formatBRL } from '../../shared/lib/money';
import { colors, spacing } from '../../shared/theme/tokens';
import { useCheckout } from './CheckoutProvider';
import { rejectionMessage, unknownMessage } from './messages';

export function CheckoutScreen() {
  const checkout = useCheckout();
  const router = useRouter();
  const { purchase } = checkout;
  const { lines, quantity, totalCents, confirmedBalanceCents, availableCents, problems } = checkout.derived;
  const state = purchase.state;

  if (state.status === 'submitting') {
    return (
      <View style={styles.center} accessible accessibilityLabel="Contratando" accessibilityState={{ busy: true }}>
        <Text accessibilityRole="header" style={styles.title}>
          Contratando…
        </Text>
        <Text style={styles.message}>Aguarde. Não feche o app nem repita a ação.</Text>
      </View>
    );
  }

  if (state.status === 'confirmed') {
    const { booking } = state;
    return (
      <ScrollView contentContainerStyle={styles.content}>
        <Text accessibilityRole="header" style={styles.title}>
          Contratação confirmada
        </Text>
        <Text style={styles.detail}>{`Código: ${booking.bookingId}`}</Text>
        {booking.items.map((item) => (
          <Text key={item.itemId} style={styles.detail}>{`${item.serviceCode}: ${formatBRL(item.unitPriceCents)}`}</Text>
        ))}
        <Row label="Valor cobrado" cents={booking.chargedAmountCents} />
        <Row label="Saldo restante" cents={booking.remainingBalanceCents} />
        <ActionButton label="Ver contratação" onPress={() => router.push(`/bookings/${booking.bookingId}`)} />
        <ActionButton label="Ver serviços" onPress={() => router.navigate('/services')} />
        <ActionButton
          label="Nova seleção"
          onPress={() => {
            purchase.dismiss();
            router.navigate('/');
          }}
        />
      </ScrollView>
    );
  }

  if (state.status === 'rejected') {
    return (
      <Settled
        title="Contratação não realizada"
        message={rejectionMessage(state.reason, state.retryAfterAt)}
        retryAfterAt={state.retryAfterAt}
        actionLabel="Voltar à revisão"
        onAction={purchase.dismiss}
      />
    );
  }

  if (state.status === 'unknown') {
    const blocked = state.cause === 'idempotency-conflict' || state.cause === 'retention-expired';
    return (
      <Settled
        title="Resultado não confirmado"
        message={unknownMessage(state.cause, state.retryAfterAt)}
        retryAfterAt={state.retryAfterAt}
        actionLabel="Verificar contratação"
        onAction={() => void purchase.verify()}
        actionHidden={blocked}
      />
    );
  }

  if (quantity === 0) {
    return (
      <View style={styles.center}>
        <Text accessibilityRole="header" style={styles.title}>
          Revisão
        </Text>
        <Text style={styles.message}>Nenhum serviço selecionado.</Text>
        <ActionButton label="Escolher serviços" onPress={() => router.navigate('/')} />
      </View>
    );
  }

  const unavailable = problems.flatMap((p) => (p.kind === 'service-unavailable' || p.kind === 'unknown-service' ? [p.code] : []));
  const preferred = checkout.selection.preferredFirstServiceCode;

  return (
    <ScrollView contentContainerStyle={styles.content}>
      <Text accessibilityRole="header" style={styles.title}>
        Revisão
      </Text>

      {checkout.priceChanges.length > 0 ? (
        <View accessibilityRole="alert" style={styles.alert}>
          {checkout.priceChanges.map((change) => (
            <Text key={change.code} style={styles.error}>
              {`O preço de ${change.name} mudou de ${formatBRL(change.fromCents)} para ${formatBRL(change.toCents)}.`}
            </Text>
          ))}
          <Text style={styles.error}>Revise o total antes de continuar.</Text>
          <ActionButton label="Aceitar novos preços" onPress={checkout.acknowledgePriceChanges} />
        </View>
      ) : null}

      {unavailable.length > 0 ? (
        <Text accessibilityRole="alert" style={styles.error}>
          {`Remova os serviços indisponíveis: ${unavailable.join(', ')}.`}
        </Text>
      ) : null}
      {problems.some((p) => p.kind === 'insufficient-balance') ? (
        <Text accessibilityRole="alert" style={styles.error}>
          Saldo insuficiente para esta seleção. Remova serviços.
        </Text>
      ) : null}
      {problems.some((p) => p.kind === 'data-unavailable') ? (
        <Text accessibilityRole="alert" style={styles.error}>
          Saldo ou catálogo indisponíveis. Os valores abaixo não estão confirmados.
        </Text>
      ) : null}

      <View style={styles.section}>
        {lines.map((line) => {
          const price = line.unitPriceCents === null ? null : formatBRL(line.unitPriceCents);
          return (
            <View key={line.code} style={styles.line}>
              <View
                style={styles.lineInfo}
                accessible
                accessibilityLabel={`${line.name}, ${line.quantity} ${line.quantity === 1 ? 'unidade' : 'unidades'}${price ? `, ${price} cada` : ''}`}
              >
                <Text style={styles.name}>{line.name}</Text>
                <Text style={styles.detail}>{price ? `${line.quantity} × ${price}` : `${line.quantity} un.`}</Text>
              </View>
              <ActionButton
                label={`Remover ${line.name}`}
                text="Remover"
                onPress={() => checkout.remove(line.code)}
              />
            </View>
          );
        })}
      </View>

      <View style={styles.section} accessibilityRole="radiogroup">
        <Text style={styles.subtitle}>Executar primeiro</Text>
        <Radio label="Sem preferência" selected={preferred === null} onPress={() => checkout.setPreference(null)} />
        {lines.map((line) => (
          <Radio
            key={line.code}
            label={line.name}
            selected={preferred === line.code}
            onPress={() => checkout.setPreference(line.code)}
          />
        ))}
      </View>

      <View style={styles.section}>
        <Row label="Subtotal da seleção" cents={totalCents} />
        <Row label="Saldo confirmado" cents={confirmedBalanceCents} />
        <Row label="Saldo estimado após contratação" cents={availableCents} />
      </View>

      <Text style={styles.notice}>
        Ao contratar, o valor é cobrado imediatamente, mesmo que o serviço entre em fila. As máquinas são simuladas.
      </Text>
      {purchase.enabled ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={totalCents === null ? 'Confirmar contratação' : `Confirmar contratação de ${formatBRL(totalCents)}`}
          accessibilityState={{ disabled: !purchase.canConfirm }}
          disabled={!purchase.canConfirm}
          onPress={() => void purchase.confirm()}
          style={[styles.cta, !purchase.canConfirm && styles.ctaDisabled]}
        >
          <Text style={styles.ctaText}>Confirmar contratação</Text>
        </Pressable>
      ) : (
        <Text accessibilityRole="alert" style={styles.notice}>
          A contratação ainda não está habilitada nesta versão.
        </Text>
      )}
    </ScrollView>
  );
}

/** Result view of a rejected or unknown purchase; its action stays disabled until Retry-After passes. */
function Settled(props: {
  title: string;
  message: string;
  retryAfterAt: number | null;
  actionLabel: string;
  onAction: () => void;
  actionHidden?: boolean;
}) {
  const { retryAfterAt } = props;
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (retryAfterAt === null) return;
    const wait = retryAfterAt - Date.now();
    if (wait <= 0) return;
    const timer = setTimeout(() => setNow(Date.now()), wait);
    return () => clearTimeout(timer);
  }, [retryAfterAt]);

  const waiting = retryAfterAt !== null && now < retryAfterAt;
  return (
    <View style={styles.center}>
      <Text accessibilityRole="header" style={styles.title}>
        {props.title}
      </Text>
      <Text accessibilityRole="alert" style={styles.error}>
        {props.message}
      </Text>
      {props.actionHidden ? null : (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={props.actionLabel}
          accessibilityState={{ disabled: waiting }}
          disabled={waiting}
          onPress={props.onAction}
          style={[styles.cta, waiting && styles.ctaDisabled]}
        >
          <Text style={styles.ctaText}>{props.actionLabel}</Text>
        </Pressable>
      )}
    </View>
  );
}

function Row({ label, cents }: { label: string; cents: number | null }) {
  const value = cents === null ? 'indisponível' : formatBRL(cents);
  return (
    <View style={styles.row} accessible accessibilityLabel={`${label}: ${value}`}>
      <Text style={styles.detail}>{label}</Text>
      <Text style={styles.name}>{value}</Text>
    </View>
  );
}

function Radio({ label, selected, onPress }: { label: string; selected: boolean; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole="radio"
      accessibilityLabel={label}
      accessibilityState={{ selected, checked: selected }}
      onPress={onPress}
      style={styles.radio}
    >
      <Text style={styles.detail}>{`${selected ? '(●)' : '( )'} ${label}`}</Text>
    </Pressable>
  );
}

function ActionButton({ label, text, onPress }: { label: string; text?: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={label} onPress={onPress} style={styles.button}>
      <Text style={styles.buttonText}>{text ?? label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: spacing.sm, padding: spacing.md },
  content: { padding: spacing.md, gap: spacing.md },
  section: { gap: spacing.sm },
  title: { fontSize: 22, fontWeight: '600', color: colors.text },
  subtitle: { fontSize: 16, fontWeight: '600', color: colors.text },
  message: { fontSize: 16, color: colors.textMuted, textAlign: 'center' },
  error: { fontSize: 16, color: colors.danger },
  alert: { gap: spacing.xs },
  notice: { fontSize: 14, color: colors.textMuted },
  line: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: spacing.sm },
  lineInfo: { flexShrink: 1 },
  row: { flexDirection: 'row', flexWrap: 'wrap', justifyContent: 'space-between', gap: spacing.sm },
  name: { fontSize: 16, fontWeight: '600', color: colors.text },
  detail: { fontSize: 16, color: colors.text },
  radio: { minHeight: 44, justifyContent: 'center' },
  cta: {
    minHeight: 48,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: spacing.md,
    backgroundColor: colors.text,
    borderRadius: 8,
  },
  ctaDisabled: { opacity: 0.4 },
  ctaText: { fontSize: 16, fontWeight: '600', color: colors.background },
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
