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
- **Phase / Task**: Código de P0 (E0 a E5) completo; Verifier independente rodou 2 iterações (1 fix loop) e terminou em PASS para o que é verificável sem a API real e sem o Android (`validation.md`, faixa `08e6c7d..db1ea75`, 252 testes, 28 mutantes mortos). Pausado por decisão do usuário (2026-10-21): tudo que exige a API real vem depois de todas as etapas.
- **Completed**: T1 a T9, T11 a T20, T22 e T27 com Done when `[x]`; T10, T21, T23 a T26, T28, T29 e T30 implementadas/preparadas (`[~]`) e pendentes de gate externo.
- **Pendente da API real e do usuário**: C1 (T10; demo Home/Checkout sem débito, saldo 5000, catálogo 1890/2090), C2 (T21; ligar `capabilities.purchase` e provar compra 5000 para 3110, replay, duplicatas, limite, códigos e retenção da chave, depois preencher `keyRetentionMs`), C3 (T23 a T26, T28), N1 (T29, executado pelo usuário com `apps/mobile/docs/native-validation.md`), e então integrar as branches por fast-forward em `mobile-worktree`. Só depois disso a entrega P0 pode ser declarada concluída; o Verifier deve ser reexecutado com esses resultados.
- **Next step**: quando o usuário avisar que a API está no docker: subir com a base demo, apontar `EXPO_PUBLIC_API_URL`, provar C1, C2 e C3 contra a API real, registrar origem/data em `apps/mobile/docs/api-contract.md`, fechar os `[~]`, integrar E1 a E5 e reexecutar o Verifier. P1 (T31 a T37) só depois de CP1.
- **Blockers**: C1/C2/C3 e N1 não provados. `dotnet` fora do PATH desta sessão (há `docker`); testes do workspace e do bootstrap que compilam a API falham por isso.
- **Branches**: cadeia linear E0 `chore/mobile-00-baseline` (já em `mobile-worktree`), E1 `feat/mobile-01-catalog-wallet` (T2 a T10), E2 `feat/mobile-02-selection-review` (T11 a T16), E3 `feat/mobile-03-safe-booking` (T17 a T21), E4 `feat/mobile-04-booking-tracking` (T22 a T28), E5 `chore/mobile-05-p0-readiness` (T29, T30 e correções do Verifier; branch atual). Cada uma contém as anteriores; nada foi enviado ao remoto.
- **Uncommitted files**: nenhum (`.omc/` agora ignorado).
