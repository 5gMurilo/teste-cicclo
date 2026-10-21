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
- **Phase / Task**: P0 fechado (T1 a T30, Verifier PASS em `cb9adcd` e delta PASS em `57b40ed`; N1 aprovado pelo usuário no iOS, aceito para o Android por AD-007). P1 implementado (T31 a T37) e ligado na branch `feat/mobile-06-service-lifecycle`.
- **P1**: T32 e T36 `[x]`; T31, T33, T34, T35 e T37 `[~]`. As quatro capacidades (`capabilities.itemCommands.*`) foram ligadas com evidência parcial na API real (seção "Provas P1 contra a API real" em `apps/mobile/docs/api-contract.md`, com o que foi e o que não foi observado), porque o usuário precisa usar as ações; o resumo de aprovação de `live/lifecycle.live.test.ts` se perdeu na primeira execução.
- **Pendente**: restaurar a demo (`--reset-demo` do backend, API parada), rodar `live/contract.live.test.ts` e `live/lifecycle.live.test.ts` com a saída salva em arquivo, completar a tabela de provas, fechar os `[~]` e rodar o Verifier de P1 (escopo QUEUE-01 a QUEUE-10).
- **Estado da demo agora**: carteira 1020 e três contratações (lavagem A cancelada; lavagem em andamento com secagem cancelada; uma secagem com oferta criada pelo app).
- **Blockers**: nenhum para uso. `dotnet` fora do PATH da sessão do agente (workspace e bootstrap que compilam a API falham por isso; não é do mobile).
- **Branches**: cadeia linear E0 a E5, E6 `feat/mobile-06-service-lifecycle` (atual); `mobile-worktree` integrada por fast-forward. Nada foi enviado ao remoto.
- **Uncommitted files**: nenhum.
