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

### AD-006
- **Decision**: O gate N1 (T29) será executado no iOS (simulador ou iPhone) em vez do Android; o resultado é registrado como "validado no iOS", e o Android só é declarado validado se for ensaiado também.
- **Reason**: Escolha do usuário por ser mais simples de executar; o app é híbrido e o código é o mesmo nas duas plataformas.
- **Trade-off**: A spec (UX-02, QUAL-02) cita Android; o iOS não prova TalkBack, fonte 200% nem os gestos do Android. Deriva consciente da spec, registrada aqui e no roteiro.
- **Scope**: T29, T30 e a definição de pronto de P0 (`apps/mobile/docs/native-validation.md`).
- **Date**: 2026-10-21
- **Status**: active

### AD-007
- **Decision**: O usuário decidiu que a validação no iOS vale também para o Android (app híbrido, comportamento igual nas duas plataformas); T29 e T30 ficam fechadas com essa decisão, sem ensaio Android.
- **Reason**: Decisão do usuário em 2026-10-21, após o relato "Validado o iOS".
- **Trade-off**: A decisão é do usuário e não é evidência: não há ensaio com TalkBack, fonte 200% nem gestos do Android, e o relato do iOS não tem detalhe por cenário. UX-02 e o smoke Android de QUAL-02 ficam aceitos por decisão, não provados.
- **Scope**: T29, T30 e a definição de pronto de P0 (substitui o pendente Android de AD-006).
- **Date**: 2026-10-21
- **Status**: active

## Handoff

- **Feature**: `.specs/features/cicclo-mobile/spec.md`.
- **Phase / Task**: Execute inline e sequencial. Última tarefa fechada: T35.
- **Completed**: P0 fechado; T32 fechada; T31, T33, T34 e T35 pendentes da prova CP1. T35 = reportIssue.ts (POST sem corpo, 202 validado, 'Relato enviado para verificação' sem qualquer texto de estorno, ação só em in_progress e uma vez por tela) (31/37 com Done when marcado).
- **In-progress**: Nenhuma.
- **Next step**: T36 (polling condicional de lista e detalhe).
- **Blockers**: nenhum para P0. `dotnet` fora do PATH da sessão do agente (workspace e testes de bootstrap que compilam a API falham por isso; não é do mobile).
- **Branches** (atual: `feat/mobile-06-service-lifecycle`): chore/mobile-00-baseline 08e6c7d; chore/mobile-05-p0-readiness 1a4ff65; feat/mobile-01-catalog-wallet 6bd529c; feat/mobile-02-selection-review 0a34316; feat/mobile-03-safe-booking 9674db3; feat/mobile-04-booking-tracking ad3d9a8; feat/mobile-06-service-lifecycle 046e364; mobile-worktree f3ac3ca.
- **Uncommitted files**: `.omc/` e `apps/mobile/.omc/` (estado de ferramenta, não versionar).
