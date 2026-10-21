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
- **Phase / Task**: Código de P0 completo (E0 a E5). Gates C1, C2 e C3 provados contra a API real em 2026-10-21 (`apps/mobile/live/contract.live.test.ts`, evidência em `apps/mobile/docs/api-contract.md`); compra real ligada (`capabilities.purchase = true`). Verifier independente: PASS em `08e6c7d..db1ea75` (iteração 2); iteração 3 sobre `9d4ad9f` deu FAIL por um único mutante de baixa gravidade (N5: `confirm()` com seleção vazia), já corrigido e coberto por `CheckoutProviderPurchase.test.tsx` (o mutante morre), mas **sem nova reverificação** porque o limite de 3 iterações foi atingido; `validation.md` registra esse FAIL.
- **Completed**: 28/37 com Done when `[x]` (T1 a T28). Gate B: 31 suítes, 254 testes, typecheck, lint e export Android/iOS verdes.
- **Pendente**: N1 (T29, executado pelo usuário no Android com `apps/mobile/docs/native-validation.md`) e, com ele, o fechamento de T30 (README já publicado, falta N1 e o Verifier final). P1 (T31 a T37) só depois de P0 verificado e de CP1.
- **Next step**: o usuário executa N1 e reporta resultado por cenário; registrar em `native-validation.md`, commit `docs(mobile): record verified android and accessibility evidence`, marcar T29/T30 e reexecutar o Verifier para o fechamento P0. A demo local está com o saldo debitado (3110) e uma contratação; para repetir `test:live` ou o cenário 3 do checklist, restaurar a demo pelo `--reset-demo` do backend (API parada antes).
- **Blockers**: N1 (usuário). `dotnet` fora do PATH da sessão do agente (workspace e testes de bootstrap que compilam a API falham por isso; não é do mobile).
- **Branches**: cadeia linear E0 `chore/mobile-00-baseline`, E1 `feat/mobile-01-catalog-wallet`, E2 `feat/mobile-02-selection-review`, E3 `feat/mobile-03-safe-booking`, E4 `feat/mobile-04-booking-tracking`, E5 `chore/mobile-05-p0-readiness`; `mobile-worktree` integrada por fast-forward até a E5. Nada foi enviado ao remoto.
- **Uncommitted files**: nenhum.
