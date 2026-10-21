# STATE

## Decisions

### AD-001
- **Decision**: O mobile será organizado por domínio, com DRY, KISS, YAGNI e SOLID obrigatórios em todo incremento; atenção explícita a S, O e L.
- **Reason**: Regra expressa do usuário; estrutura deve revelar catálogo, carteira, checkout e contratações, não camadas técnicas genéricas.
- **Trade-off**: Renunciamos a hierarquias, repositórios genéricos e extensibilidade hipotética; abstrações surgem só com necessidade real.
- **Scope**: Planejamento e futura implementação em `apps/mobile`.
- **Date**: 2026-10-21
- **Status**: active

### AD-002
- **Decision**: Cada etapa de desenvolvimento incremental terá uma nova branch, com commits locais atômicos e testes no mesmo commit do comportamento.
- **Reason**: Exigência explícita de isolamento por etapa e histórico revisável.
- **Trade-off**: Etapas dependem de integração ordenada; não acumular toda a implementação em uma branch ou commit único.
- **Scope**: Fluxo Git futuro do mobile. Criar branches/commits de implementação depende de aprovação e início de Execute.
- **Date**: 2026-10-21
- **Status**: active

### AD-003
- **Decision**: Comandos financeiros não serão pausados/enfileirados pelo cliente para retomada automática após reconexão; reenvio exige ação explícita com a intenção original.
- **Reason**: `retry: false` sozinho não impede pausa offline no TanStack Query. É necessário controlar networkMode e prontidão antes do envio para cumprir a recuperação financeira explícita.
- **Trade-off**: Não usamos fila offline/dehydration de mutations para compra; falhas incertas requerem interação do usuário, preservando chave e payload.
- **Scope**: Contratação e futuras operações financeiras do mobile; design e tarefas pré-aprovados nesta conversa.
- **Date**: 2026-10-21
- **Status**: active

### AD-004
- **Decision**: Testes mínimos e escopados: cada tarefa testa só o que alterou (2–4 casos focados); gates por tarefa rodam apenas testes dos arquivos alterados, suíte completa só no fechamento de etapa. Maior profundidade apenas em T12, T18–T21 (risco financeiro).
- **Reason**: Pedido expresso do usuário por máxima objetividade, sem re-testar tudo a cada implementação.
- **Trade-off**: Menor rede de segurança contra regressões entre tarefas até o gate B de cada etapa.
- **Scope**: Toda a execução de `tasks.md` (substitui os "mínimo N casos" originais por teto).
- **Date**: 2026-10-21
- **Status**: active

### AD-005
- **Decision**: A validação Android real (gate N1, T29) é executada pelo usuário; o Claude prepara o checklist e registra os resultados reportados.
- **Reason**: O usuário tem o aparelho e prefere conduzir o smoke manual.
- **Trade-off**: T29/T30 só fecham quando o usuário reportar; não bloqueia as etapas anteriores.
- **Scope**: T29, T30 e DoD P0.
- **Date**: 2026-10-21
- **Status**: active

## Handoff

- **Feature**: `.specs/features/cicclo-mobile/spec.md`.
- **Phase / Task**: Execute inline e sequencial. Última tarefa fechada: T25.
- **Completed**: T1-T9, T11-T20 e T22 fechadas; T10, T21, T23, T24 e T25 implementadas e pendentes de gate com a API real. T25 = bookings/detailQuery.ts (id validado como UUID antes de qualquer GET, enabled=false e guarda no queryFn; 404 recuperável; schema compartilhado com a listagem por ser a mesma projeção) (20/37 com Done when marcado).
- **In-progress**: Nenhuma.
- **Next step**: T26 (BookingDetailsScreen + rota bookings/[id], retorno a Serviços).
- **Pendente da API real**: C1 (T10 e demo de E1/E2), integração por fast-forward de E1 e E2 em `mobile-worktree`; C2 (CTA real em T21), C3 (T22-T28), N1 (T29, com o usuário). Decisão do usuário (2026-10-21): implementar o código de todas as etapas E com fakes primeiro e deixar a interação real com a API para depois de todas elas.
- **Blockers**: C1/C2/C3 não provados. `dotnet` não aparece no PATH desta sessão (há `docker` e `psql`). `tests/bootstrap.test.mjs` teste 6 falha localmente por isso.
- **Branches** (atual: `feat/mobile-04-booking-tracking`): chore/mobile-00-baseline 08e6c7d; feat/mobile-01-catalog-wallet 6bd529c; feat/mobile-02-selection-review 0a34316; feat/mobile-03-safe-booking 9674db3; feat/mobile-04-booking-tracking 58ba545; mobile-worktree 08e6c7d.
- **Uncommitted files**: `.omc/` e `apps/mobile/.omc/` (estado de ferramenta, não versionar).
