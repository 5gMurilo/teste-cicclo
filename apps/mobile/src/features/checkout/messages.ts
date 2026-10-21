import type { RejectionReason, UnknownCause } from './useBookingSubmission';
import type { AddCheck } from './checkoutSelectors';

type BlockedReason = Extract<AddCheck, { ok: false }>['reason'] | 'purchase-pending';

const MESSAGES: Record<BlockedReason, string> = {
  'insufficient-balance': 'Saldo insuficiente para adicionar este serviço.',
  'over-limit': 'Limite de 10 serviços por contratação.',
  'data-unavailable': 'Aguarde o saldo e o catálogo serem confirmados para adicionar serviços.',
  'service-unavailable': 'Este serviço não está disponível para contratação agora.',
  'unknown-service': 'Este serviço não está mais no catálogo.',
  'invalid-quantity': 'A quantidade selecionada é inválida.',
  overflow: 'O valor da seleção excede o limite permitido.',
  'purchase-pending': 'Há uma contratação em andamento. Aguarde o resultado antes de alterar a seleção.',
};

export function blockedMessage(reason: BlockedReason): string {
  return MESSAGES[reason];
}

const NOT_CHARGED = 'Nenhuma cobrança foi feita.';

const REJECTIONS: Record<Exclude<RejectionReason, 'rate-limited'>, string> = {
  'insufficient-balance': `Saldo insuficiente. ${NOT_CHARGED} O saldo foi atualizado.`,
  'price-changed': `O preço mudou. ${NOT_CHARGED} Revise o novo total e confirme novamente.`,
  'service-disabled': `Algum serviço ficou indisponível. ${NOT_CHARGED} Remova-o e revise.`,
  'no-operational-machine': `Algum serviço está sem máquina operacional. ${NOT_CHARGED} Remova-o e revise.`,
  'service-not-found': `Algum serviço não existe mais no catálogo. ${NOT_CHARGED}`,
  'invalid-request': `A solicitação foi recusada por ser inválida. ${NOT_CHARGED}`,
  'request-too-large': `A solicitação não pôde ser processada. ${NOT_CHARGED}`,
  'unsupported-media-type': `A solicitação não pôde ser processada. ${NOT_CHARGED}`,
  configuration: `O app não está configurado para acessar a API. ${NOT_CHARGED}`,
};

const timeFormat = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

export function rejectionMessage(reason: RejectionReason, retryAfterAt: number | null): string {
  if (reason !== 'rate-limited') return REJECTIONS[reason];
  const wait = retryAfterAt === null ? 'Aguarde um instante' : `Aguarde até ${timeFormat.format(new Date(retryAfterAt))}`;
  return `Muitas tentativas. ${NOT_CHARGED} ${wait} e tente novamente.`;
}

const UNKNOWN_BASE =
  'Não foi possível confirmar se a contratação foi concluída. Verifique para saber o resultado: a verificação não cobra de novo.';

export function unknownMessage(cause: UnknownCause, retryAfterAt: number | null): string {
  switch (cause) {
    case 'idempotency-conflict':
      return 'Esta contratação não pode ser verificada automaticamente (a chave já pertence a outra intenção). Consulte a aba Serviços antes de tentar novamente.';
    case 'retention-expired':
      return 'O prazo para verificar esta contratação passou. Consulte a aba Serviços antes de decidir.';
    case 'rate-limited':
      return `${UNKNOWN_BASE} ${retryAfterAt === null ? 'Aguarde um instante.' : `Aguarde até ${timeFormat.format(new Date(retryAfterAt))}.`}`;
    default:
      return UNKNOWN_BASE;
  }
}
