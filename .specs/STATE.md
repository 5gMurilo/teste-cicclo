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

### AD-008
- **Decision**: Adicionar ao app a ação demo "Concluir (simulação)" no detalhe de uma execução em andamento, opt-in por `EXPO_PUBLIC_DEMO_SIMULATION=true`, rotulada como simulação, que chama `POST /api/v1/simulation/machines/{machineId}/complete`.
- **Reason**: Pedido do usuário em 2026-10-21, depois de ligar o DemoMode na API: ele quer concluir serviços pelo app para demonstrar o ciclo completo.
- **Trade-off**: Contraria a regra da spec "sem painel de operador" (T35) e a rota só existe em Development com DemoMode. Mitigação: oculto por padrão, rótulo explícito de simulação, capacidade e variável de ambiente, e comportamento claro quando a rota não existe.
- **Scope**: apps/mobile, detalhe da contratação; fora de QUEUE-01 a QUEUE-10 (sem requisito de spec).
- **Date**: 2026-10-21
- **Status**: active

## Handoff

- **Feature**: `.specs/features/cicclo-mobile/spec.md`.
- **Phase / Task**: P0 (T1 a T30) e P1 (T31 a T37) implementados, ligados e verificados pelo Verifier independente (PASS em `749e1b6`, 361 testes; ver `validation.md`). N1 aprovado pelo usuário no iOS e aceito para o Android (AD-006, AD-007). Ação demo "Concluir (simulação)" adicionada por decisão do usuário (AD-008), opt-in por `EXPO_PUBLIC_DEMO_SIMULATION=true`, validada pelo usuário no app contra a API real.
- **Completed**: 35/37 com Done when `[x]`; T34 (cancelar) e T37 (oferta na lista) seguem `[~]` à espera da prova ao vivo limpa.
- **Pendente (externo, não força FAIL no Verifier)**: restaurar a demo (`--reset-demo`) e rodar `live/contract.live.test.ts`, `live/lifecycle.live.test.ts` e `live/simulation.live.test.ts` com a saída salva em arquivo; item `queued` real, corpo real do cancelamento e repetições; mesmo `incidentId` na repetição do relato; 404 da rota de simulação fora do DemoMode (inferido); compra após horas; compra ao vivo de duas ou mais unidades com preferência aceita; Android sem ensaio (aceito por decisão).
- **Estado da demo**: carteira 1020, máquinas livres, várias contratações (a lavagem e a secagem das compras de teste concluídas pela simulação).
- **Blockers**: nenhum para uso. `dotnet` fora do PATH da sessão do agente (workspace e bootstrap que compilam a API falham por isso; não é do mobile).
- **Branches**: pilha linear sobre `feat/bootstrap-monorepo`, sem numeração no nome: `docs/mobile-delivery-baseline`, `feat/mobile-catalog-and-wallet-reading`, `feat/mobile-service-selection-and-review`, `feat/mobile-safe-booking-purchase`, `feat/mobile-booking-tracking`, `chore/mobile-p0-validation-and-evidence` e `feat/mobile-service-lifecycle` (atual); `mobile-worktree` é só a branch local deste worktree. Publicadas no GitHub como PRs empilhados, sem merge até o sinal do usuário.
- **Uncommitted files**: nenhum.
