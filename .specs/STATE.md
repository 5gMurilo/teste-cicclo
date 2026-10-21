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

## Handoff

- **Feature**: `.specs/features/cicclo-mobile/spec.md`.
- **Phase / Task**: P0 completo (T1 a T30). C1, C2 e C3 provados contra a API real em 2026-10-21; N1 aprovado pelo usuário no iOS (AD-006), relato global sem detalhe por cenário (`apps/mobile/docs/native-validation.md`). Android não ensaiado.
- **Completed**: 30/37 com Done when `[x]`. Gate B: 32 suítes, 260 testes, typecheck, lint e export verdes (o export exige `EXPO_PUBLIC_API_URL` apenas para as leituras em runtime, não para exportar).
- **Verifier**: PASS na rodada 4 sobre `cb9adcd`. Depois disso entraram 4 commits de correções de uso no iOS (log de desenvolvimento, mensagem de configuração ausente, `anchor`, README) e o registro do N1; a verificação delta final desses commits é o último passo de P0.
- **Pendente**: verificação delta final do Verifier; Android (opcional); ícones das abas (cosmético, exige `@expo/vector-icons`); compra de duas ou mais unidades e preferência aceita ao vivo (exige restaurar a demo); retenção da chave após horas. P1 (T31 a T37) só depois de CP1 e de decisão do usuário.
- **Next step**: concluir a verificação delta; depois, decidir com o usuário se segue para P1 e quais endpoints de ciclo de vida já estão prontos na API.
- **Blockers**: nenhum para P0. `dotnet` fora do PATH da sessão do agente (workspace e testes de bootstrap que compilam a API falham por isso; não é do mobile).
- **Branches**: cadeia linear E0 a E5 (`chore/mobile-05-p0-readiness` é a atual); `mobile-worktree` integrada por fast-forward. Nada foi enviado ao remoto.
- **Uncommitted files**: nenhum.
