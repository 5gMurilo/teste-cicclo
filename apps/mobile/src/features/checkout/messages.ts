import type { AddCheck } from './checkoutSelectors';

type BlockedReason = Extract<AddCheck, { ok: false }>['reason'];

const MESSAGES: Record<BlockedReason, string> = {
  'insufficient-balance': 'Saldo insuficiente para adicionar este serviço.',
  'over-limit': 'Limite de 10 serviços por contratação.',
  'data-unavailable': 'Aguarde o saldo e o catálogo serem confirmados para adicionar serviços.',
  'service-unavailable': 'Este serviço não está disponível para contratação agora.',
  'unknown-service': 'Este serviço não está mais no catálogo.',
  'invalid-quantity': 'A quantidade selecionada é inválida.',
  overflow: 'O valor da seleção excede o limite permitido.',
};

export function blockedMessage(reason: BlockedReason): string {
  return MESSAGES[reason];
}
