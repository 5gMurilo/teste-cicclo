# Cicclo mobile: tarefas executáveis

**Status:** Approved, pré-aprovado expressamente pelo usuário nesta conversa em 2026-10-21, sem Plannotator.
**Spec:** [spec.md](spec.md). **Design:** [design.md](design.md).
**Execução:** em andamento. Última tarefa concluída: T30 (21/37 com Done when marcado); ver STATE.md.

## Execution Protocol

Implementar com a skill **tlc-spec-driven**, ativada pelo nome, seguindo seu fluxo Execute e Critical Rules. Se não puder ativá-la, parar. Antes de Execute, ler completamente `implement.md` da skill e rodar `validate_tasks.py`.

- Uma tarefa = um resultado coeso, testes correspondentes e **um commit atômico**. As 21 intenções macro da spec foram refinadas em 37 tarefas menores, sem ampliar escopo.
- `Where` indica o módulo principal, não uma licença para espalhar regras. Teste, fixture, manifest/lock necessário e glue mínimo ao consumidor são parte do mesmo resultado. Somente T5 exige migração simultânea de entrypoint/configuração/rotas, por ser indivisível para manter o app executável.
- `Done when` inclui o Gate indicado, casos enumerados em Tests, revisão DRY/KISS/YAGNI/SOLID e atualização de tarefa/rastreabilidade **antes** do commit. Marcar [x] só após prova, não por pré-aprovação.
- **Política de testes (revisada em 2026-10-21, decisão do usuário, AD-004):** máxima objetividade. Cada tarefa testa **somente o que ela altera**, com 2–4 casos focados no comportamento novo; não re-testar o que tarefas anteriores já cobrem. O número em `Tests` de cada tarefa é **teto sugerido, não obrigação**. Exceção de profundidade: T12, T18, T19, T20 e T21 (cálculo em centavos, idempotência, duplo toque, resultado incerto, GET antigo sobrescrevendo saldo), onde o erro debita em duplicidade ou mostra saldo errado, podem usar até o teto. Registrar contagem real por commit; não usar asserção de contagem como prova.
- Gates por tarefa executam apenas typecheck, lint e os **testes dos arquivos alterados** (ver Gate Check Commands). A suíte mobile completa roda só no fechamento de etapa (gate B). Isso não autoriza pular teste de comportamento novo nem afrouxar teste preexistente que falhe. Exceção já aprovada: trocar o contrato obsoleto da tela estática por comportamento de Router/leitura em T5/T7/T10.
- **Validação Android (N1/T29) é executada pelo próprio usuário (AD-005)**, não por mim. Eu apenas preparo o checklist; T29 e T30 só fecham com os resultados que o usuário reportar.
- Cada etapa E0–E6 tem branch nova. As subdivisões de E1 são lotes técnicos na **mesma branch E1**, não novas etapas de produto.
- Não executar P1 antes de P0 verificado. P2 permanece opcional fora desta lista executável; ver procedimento em design.md. Pré-aprovação não altera esse corte.
- Não abrir Plannotator novamente para este design/tasks. Nova dúvida de produto, quebra contratual ou ação externa continua exigindo decisão específica quando surgir.

### Ferramentas por tarefa

Padrão pré-aprovado de planejamento: read/grep/glob para reconhecer, patch para editar, shell para gates/Git local; Context7 para APIs de biblioteca. Código usa tlc-spec-driven + coding-guidelines; commits usam a política Conventional Commits da skill e podem usar well-commited sem push. Cada tarefa especifica somente acréscimos a esse padrão.

Nenhuma tarefa requer Jira, Workforce, upload de artefatos, PR ou deploy. Antes de Execute, confirmar seleção de ferramentas se houver exigência adicional da skill e oferecer lotes de implementação por subagentes, sem dispará-los por inferência desta pré-aprovação. O Verifier independente de fechamento segue obrigatório.

## Test Coverage Matrix

> Derivada da base existente, das regras aprovadas em spec.md e dos handoffs. Fontes: `apps/mobile/package.json:6–35`, `apps/mobile/__tests__/App.test.tsx:5–16`, `tests/bootstrap.test.mjs:18–45` e README raiz §Validação. Somente um teste mobile existe; foi lido integralmente. Não há guideline de cobertura percentual local. Cobertura limitada ao que cada tarefa altera (ver Política de testes no Execution Protocol), sem E2E automatizado.

| Code Layer | Required Test Type | Coverage Expectation | Location Pattern | Run Command |
| --- | --- | --- | --- | --- |
| Config runtime e normalização de erro | unit | Sucesso, limites, ausência/malformação, Retry-After e dados desconhecidos | `src/shared/**/__tests__/*.test.ts` | `pnpm --filter @cicclo/mobile test` |
| Transporte HTTP | unit | Métodos/headers/signal/timeout/status/corpo inválido; substituto async fiel | `src/shared/api/__tests__/*.test.ts` | `pnpm --filter @cicclo/mobile test` |
| Endpoint/query/schema | integration | Contrato validado, sucesso/erro/abort e QueryClient real isolado | `src/features/**/__tests__/*.test.ts` | `pnpm --filter @cicclo/mobile test` |
| Reducer/seletores/status | unit | Todos os ramos aplicáveis, 1:1 com ACs e bordas listadas | `src/features/**/__tests__/*.test.ts` | `pnpm --filter @cicclo/mobile test` |
| UI/Router/provider | integration | Interações por role/label, loading/empty/error/retry/disabled; estado entre telas | `__tests__/*.test.tsx` ou `src/features/**/__tests__/*.test.tsx` | `pnpm --filter @cicclo/mobile test` |
| Compra/cache/recovery | integration | Resultados financeiros definidos na spec, corrida, replay e zero POST sem gesto | `src/features/checkout/__tests__/*.test.tsx` | `pnpm --filter @cicclo/mobile test` |
| Lifecycle/tempo/polling | integration | Fake clock/eventos, active/blur/online/terminal, cleanup e número de chamadas | `src/**/__tests__/*.test.tsx` | `pnpm --filter @cicclo/mobile test` |
| Documentação/baseline | manual | Conferir links, Git, comandos e evidência real; não há código novo para testes unitários | `.specs/features/cicclo-mobile/`, `apps/mobile/docs/`, README | Gate D + gates herdados |
| Native/a11y real | manual | Android com fonte 200%, leitor de tela, gestos, API, rede e suspensão | `apps/mobile/docs/native-validation.md` | Gate N1; `pnpm --filter @cicclo/mobile android` apenas inicia o app |

Nenhuma tarefa que altera comportamento transfere sua cobertura para outra tarefa. Helpers novos são introduzidos e exercitados pelo primeiro teste consumidor: QueryClient com retry desativado em testes, renderWithProviders e fetch fake no limite, sem ambiente de rede externo durante Jest. Testes ficam **fora** de `src/app`, para não se tornarem rotas.

## Gate Check Commands

> Comandos extraídos do package.json real. Executar da raiz da base integrada. Não funcionam na árvore vazia antes de T1.

| Gate | Quando | Comandos/resultado exigidos |
| --- | --- | --- |
| Q (Quick) | Regras puras/transporte | `pnpm --filter @cicclo/mobile typecheck`; `pnpm --filter @cicclo/mobile lint`; teste **apenas dos arquivos alterados** (`pnpm --filter @cicclo/mobile test <caminhos>` ou `test --findRelatedTests <arquivo>`, **sem** `--`: confirmado em T1 que o script repassa argumentos ao Jest e que `--` gera falso positivo "No tests found"), cada comando separado com exit 0 |
| F (Full) | UI/endpoint/provider/cache/lifecycle | Mesmos comandos de Q, escopados aos arquivos alterados e aos seus testes de integração diretos; não criar outro script fictício |
| B (Build) | Configuração/Router e fechamento de etapa | typecheck + lint + **suíte mobile completa** + `pnpm --filter @cicclo/mobile build` com export Android/iOS válido |
| D (Docs) | Baseline/evidência/README | Inspeção de conteúdo/links e diff; nenhum resultado não executado declarado PASS; executar B quando a tarefa fecha etapa ou muda onboarding |
| W (Workspace) | Fechamento P0/P1, quando dependências do ambiente permitirem | `pnpm build`, `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm test:bootstrap`, separados. Falha externa é registrada, não corrigida alterando API |
| S (Structure) | Antes de iniciar e ao alterar planejamento | `python3 <skill-dir>/scripts/validate_spec.py cicclo-mobile --strict`; `python3 <skill-dir>/scripts/validate_tasks.py cicclo-mobile --strict` |
| CC (Commit) | Cada commit | `python3 <skill-dir>/scripts/check_commit.py --message "<mensagem da tarefa>"`; revisar staging explícito e árvore de trabalho |
| V (Verifier) | Depois do último commit de P0 e de P1 | Verifier independente + sensor isolado + `python3 <skill-dir>/scripts/validate_state.py cicclo-mobile`; sem relatório PASS com file:line, não fechar entrega |

`<skill-dir>` é resolvido pela skill ativa, não pelo projeto. Não criar scripts locais com nomes idênticos aos da skill. Gates documentais não provam produto executado.

### Gates de dependência externa

| Gate | Condição binária e evidência | Bloqueia |
| --- | --- | --- |
| C0 | Bootstrap commitado acessível, status reconciliado, sem conflito; ancestralidade permite fluxo local não destrutivo | T1 e entrada E1 |
| C1 | Wallet/services reais e seus schemas/códigos registrados com fonte/data/hash quando houver OpenAPI; UI coincide com API | Saída E1/E2 |
| C2 | POST/replay 201/200 com mesmo ID, shape, duplicatas/limite/preferência, códigos definitivos, retenção de chave e consistência monetária comprovados | Habilitar CTA real em T21 e saída E3 |
| C3 | DTOs de GET list/detail, UUID, status, ordenação e paginação efetivos conhecidos | Saída E4 e DoD P0 |
| N1 | **Executado pelo usuário** (AD-005): Android com API real, fonte 200%, leitor de tela, gestos, navegação, rede e foreground; cada cenário com resultado | T29/T30 e DoD P0 |
| CP1 | Gate P0 com Verifier PASS e cada endpoint/capacidade P1 comprovado individualmente | Entrada E6 e ações habilitadas |

**BLOCKED não é DONE.** Pode haver commit de código testado localmente com tipos temporários documentados, mas etapa não é integrada/apresentada como funcional sem seu gate externo. Se C2 não estiver resolvido, T21 não habilita compra real. Nenhuma falha de dependência é corrigida ampliando escopo para backend.

## Execution Plan

### Branches e integração local

| Etapa | Branch nova | Tarefas | Saída |
| --- | --- | --- | --- |
| E0 | `chore/mobile-00-baseline` | T1 | C0, B, S |
| E1 | `feat/mobile-01-catalog-wallet` | T2–T10 | B + C1 |
| E2 | `feat/mobile-02-selection-review` | T11–T16 | B + demo C1 sem débito |
| E3 | `feat/mobile-03-safe-booking` | T17–T21 | B + C2 + demo compra/replay |
| E4 | `feat/mobile-04-booking-tracking` | T22–T28 | B + C3 + demo leitura/lifecycle |
| E5 | `chore/mobile-05-p0-readiness` | T29–T30 | B + W + N1 + V P0 |
| E6 | `feat/mobile-06-service-lifecycle` | T31–T37 | CP1 + B + W + demo P1 + V P1 |

Default operacional detalhado para futura execução: usar a branch local **`mobile-worktree` como integração**, sem alterar `main` ou o outro worktree. Em T1, criar E0 diretamente do bootstrap commitado vigente, levando somente os documentos de planejamento deste worktree após conferir que não há colisão. Assim se preserva o histórico original sem copiar código nem cherry-pickar o monorepo.

Depois do commit/gates E0, atualizar `mobile-worktree` somente por fast-forward local se ela continuar ancestral. Cada etapa seguinte é criada dessa integração e volta por fast-forward após seus gates. Preservar branches e commits atômicos; sem squash/rebase/reset/force. Se a base avançou/divergiu, parar para reconciliação, não escolher merge destrutivo. Essa escolha resolve a premissa operacional da spec sob a pré-aprovação atual; **não executar esses comandos agora**.

Git push, PR, merge remoto, deploy, build externo pago e resets de ambiente não estão autorizados. Criar uma nova etapa não significa remover a anterior. Verificar status antes de cada troca de branch e nunca tocar no `.gitignore` local do outro worktree.

### Ordem e lotes

Execução é sequencial, uma tarefa por vez. O predecessor imediato é dependência de liberação mesmo quando não é import direto; `Reuses` descreve as dependências técnicas. A cadeia global abaixo é a única fonte do diagrama, evitando grafos diferentes da definição.

```text
T1 -> T2 -> T3 -> T4 -> T5 -> T6 -> T7 -> T8 -> T9 -> T10 -> T11 -> T12 -> T13 -> T14 -> T15 -> T16 -> T17 -> T18 -> T19 -> T20 -> T21 -> T22 -> T23 -> T24 -> T25 -> T26 -> T27 -> T28 -> T29 -> T30 -> T31 -> T32 -> T33 -> T34 -> T35 -> T36 -> T37
```

Lotes sugeridos, somente se o usuário aceitar workers em Execute: T1–T7; T8–T16; T17–T21; T22–T28; T29–T30; T31–T37 condicional. Cada lote respeita fases inteiras abaixo. O lote T8–T16 tem 9 tarefas curtas para não dividir E1-b/E2; se o orçamento efetivo exigir, atribuir uma fase inteira por worker. Nunca iniciar lote seguinte antes da conclusão/gates do anterior. Sem workers, seguir a mesma ordem inline.

## Task Breakdown

Todas as tarefas herdam o protocolo de conclusão, Gate + CC e padrão de ferramentas. As checkboxes representam execução futura. Testes e mínimos são obrigações, não resultados atuais.

### Phase 1: E0, baseline reconciliado

#### T1: registrar baseline executável sobre o bootstrap existente
**Where**: `.specs/features/cicclo-mobile/baseline.md`
**Depends on**: None
**Reuses**: Bootstrap commitado, spec/design/tasks e scripts reais; não copiar worktree sujo.
**Requirement**: QUAL-02, AD-002; pré-condição de todos os requisitos.
**What**: Reconciliar Git/versões, estabelecer a branch E0 conforme fluxo local e versionar planejamento/evidência num commit documental.
**Done when**: [x] C0 e S passam; branch nova tem bootstrap como ancestral; B reproduzido, contagem real registrada; documentos rastreados; outro worktree preservado; nenhuma feature nova.
**Tests**: manual — conferência Git/lock/strict e execução da suíte existente (baseline anterior: 1 teste; reconferir, nunca presumir número atual).
**Gate**: B + D + S + CC.
**Commit**: `docs(mobile): establish incremental delivery baseline`

### Phase 2: E1-a, configuração e navegação

#### T2: validar configuração pública do mobile
**Where**: `apps/mobile/src/shared/config/env.ts`
**Depends on**: T1
**Reuses**: Toolchain existente, API URL do ambiente a confirmar.
**Requirement**: NAV-03, SAFE-02, QUAL-01.
**What**: Ler origem API e nome demo, criar erro de configuração tratável e `.env.example` não secreto do app.
**Done when**: [x] Nome vazio vira Visitante; origem HTTP(S) não contém credenciais/path/query/hash; nenhuma URL silenciosa de fallback; acesso a env compatível com bundling Expo.
**Tests**: unit — teto de 6 casos: nome definido/vazio, URL válida, ausente, malformada e com caminho/credencial indevida; nenhum segredo na fixture.
**Gate**: Q + CC.
**Commit**: `feat(config): validate public mobile environment`

#### T3: normalizar erros e prazo de rate limit
**Where**: `apps/mobile/src/shared/api/apiError.ts`
**Depends on**: T2
**Reuses**: Modelos de erro da spec; Zod instalado neste primeiro consumidor.
**Requirement**: SAFE-01, SAFE-02, BUY-12, BUY-13.
**What**: Definir erro tipado, parser de ProblemDetails desconhecido e deadline Retry-After por segundos/HTTP-date.
**Done when**: [x] Status/errorCode/traceId sanitizados são preservados; detail não dirige regra; payload malformado não vira sucesso ou saldo default; não classifica sozinho toda recusa financeira.
**Tests**: unit — teto de 8 casos: ProblemDetails válido, campos ausentes/tipo errado, erro desconhecido, detail alterado sem mudar classificação, Retry-After segundos/data/passado/inválido.
**Gate**: Q + CC.
**Commit**: `feat(api): normalize problem details and retry deadlines`

#### T4: implementar transporte HTTP único
**Where**: `apps/mobile/src/shared/api/client.ts`
**Depends on**: T3
**Reuses**: env, normalizador e fetch nativo.
**Requirement**: SAFE-01, SAFE-02, BUY-01, BUY-08.
**What**: Centralizar prefixo/headers, parse JSON, timeout 15s e AbortSignal, retornando unknown para o endpoint validar.
**Done when**: [x] Prefixo aparece uma vez; GET propaga signal; POST preserva header/body recebidos; timeout/cleanup funcionam; transporte não inventa chave ou rollback.
**Tests**: unit — teto de 9 casos: GET, POST/header/body, JSON sucesso, corpo vazio, HTML, JSON inválido, não-2xx, abort externo, timeout e limpeza; fake fetch async fiel.
**Gate**: Q + CC.
**Commit**: `feat(api): centralize cancellable json transport`

#### T5: migrar navegação para Expo Router
**Where**: `apps/mobile/src/app/_layout.tsx`
**Depends on**: T4
**Reuses**: app Expo existente; config pública; dependências Router e pares resolvidos pelo SDK 58.
**Requirement**: NAV-01, NAV-02, BOOK-07, UX-01, UX-02.
**What**: Migrar entrypoint/config, root e rotas finas numa única troca executável; tabs services/index/checkout com Home inicial e estados honestos temporários.
**Done when**: [x] Ordem/inicial corretas; sem lógica de domínio nas rotas; safe area sem duplicação; teste antigo migrado por requisito aprovado; sem catálogo fake.
**Tests**: integration — teto de 4 casos de Router: Home inicial, ordem das tabs, navegação para cada aba, placeholders honestos sem POST; usar submódulo de testes compatível, fora da pasta app.
**Gate**: B + CC; export é obrigatório por mudar entrypoint.
**Tools**: Context7 para Expo Router e migração de testes; nenhuma atualização de Expo/RN por preferência.
**Commit**: `feat(navigation): compose the three domain tabs`

### Phase 3: E1-b, leitura real e apresentação

#### T6: consumir a carteira autoritativa
**Where**: `apps/mobile/src/features/wallet/queries.ts`
**Depends on**: T5
**Reuses**: Transporte e Zod; QueryClient criado nesta primeira query, estável no root.
**Requirement**: READ-01, READ-02, READ-03, SAFE-01.
**What**: Implementar schema, GET com signal, key/options públicos de wallet e configuração inicial Query.
**Done when**: [x] Inteiro seguro BRL/updatedAt validado pelo contrato; sem default monetário; QueryClient único; helper de testes com cache isolado e retries desativados.
**Tests**: integration — teto de 5 casos: wallet válida, centavos inválidos/fora da faixa segura, campo obrigatório ausente, erro e retry, cancelamento/signal; schema/tipos compatíveis se OpenAPI disponível.
**Gate**: F + CC; C1 necessário para saída E1.
**Commit**: `feat(wallet): query validated confirmed balances`

#### T7: apresentar header compartilhado sem autoridade financeira
**Where**: `apps/mobile/src/shared/layout/AppHeader.tsx`
**Depends on**: T6
**Reuses**: Props conectadas pelo layout à query wallet; env; money formatter e tokens mínimos introduzidos com este primeiro consumidor.
**Requirement**: NAV-03, READ-01, READ-02, READ-03, UX-01, UX-02.
**What**: Renderizar saudação à esquerda e saldo/status à direita; layout fornece dados/retry, shared não importa features.
**Done when**: [x] Loading não mostra moeda; falha tem retry; valor é BRL real; nome longo/fonte ampliada têm quebra controlada; um dono de inset; formatador não aceita dinheiro inválido.
**Tests**: integration — teto de 6 casos: nome/default, wallet 5000, loading, erro/retry, saldo desatualizado rotulado, labels e font scaling; unitário de BRL/inteiro junto ao helper criado.
**Gate**: F + CC.
**Commit**: `feat(wallet): present shared balance and greeting states`

#### T8: consumir catálogo e elegibilidade
**Where**: `apps/mobile/src/features/catalog/queries.ts`
**Depends on**: T7
**Reuses**: Transporte, QueryClient, Zod e padrão público wallet.
**Requirement**: READ-04, READ-05, READ-06, SAFE-01.
**What**: Implementar o único GET catálogo com schema próprio, key e options; documentar contrato real ou temporário.
**Done when**: [x] Preço/nome vêm da resposta; códigos/desativação/motivos preservados; sem tipos manuais paralelos a gerados; registry contratual registra origem do schema.
**Tests**: integration — teto de 5 casos: catálogo válido, preço variável, vazio, item indisponível, obrigatório inválido/erro; fixtures não entram no bundle real.
**Gate**: F + CC; C1 necessário para saída E1.
**Commit**: `feat(catalog): query authoritative service availability`

#### T9: criar card de serviço acessível
**Where**: `apps/mobile/src/features/catalog/components/ServiceCard.tsx`
**Depends on**: T8
**Reuses**: Tipo público do catálogo, tokens/money; sem acessar Context ou HTTP.
**Requirement**: READ-04, READ-06, UX-01.
**What**: Apresentar nome/preço/estado textual; aceitar quantidades/callbacks opcionais para seleção futura sem fingir compra habilitada.
**Done when**: [x] Serviço indisponível tem motivo, não só cor; ação não aparece funcional sem callback; props pequenas e labels incluem serviço/preço.
**Tests**: integration — teto de 4 casos: serviço disponível, indisponível/desativado, preço da prop e ação ausente/desabilitada; disabled não chama callback.
**Gate**: F + CC.
**Commit**: `feat(catalog): render accessible service cards`

#### T10: compor Home de leitura com recuperação
**Where**: `apps/mobile/src/features/home/HomeScreen.tsx`
**Depends on**: T9
**Reuses**: Query pública catálogo, ServiceCard, header já conectado no layout.
**Requirement**: READ-04, READ-05, READ-06, BOOK-07, UX-01.
**What**: Conectar Home ao catálogo e oferecer loading/empty/error/retry, ainda sem seleção ou POST.
**Done when**: [~] Rota só compõe screen; nenhuma regra de preço no JSX; demo C1 confirma dados reais; E1 pronta somente com B+C1.
**Tests**: integration — teto de 5 casos: loading, ready, vazio, erro/retry, serviço indisponível; afirmar ausência de POST.
**Gate**: F + B no fechamento E1 + C1 + CC.
**Commit**: `feat(home): compose live catalog reading states`

### Phase 4: E2, seleção e revisão sem débito

#### T11: modelar quantidades e preferência local
**Where**: `apps/mobile/src/features/checkout/checkoutReducer.ts`
**Depends on**: T10
**Reuses**: Nenhum estado remoto; só tipos e ações de seleção.
**Requirement**: SEL-05, SEL-07, SEL-09.
**What**: Implementar add/remove/setPreference/clear como transformação pura sem fetch.
**Done when**: [x] Remove elimina zeros; preferência inexistente não é mantida; remover última ocorrência preferida limpa preferência; input não é mutado.
**Tests**: unit — teto de 7 casos: add inicial/repetido, remove, remove ausente, preferência válida, última preferida removida, clear e imutabilidade.
**Gate**: Q + CC.
**Commit**: `feat(checkout): model local quantities and preference`

#### T12: derivar seleção financeira em centavos seguros
**Where**: `apps/mobile/src/features/checkout/checkoutSelectors.ts`
**Depends on**: T11
**Reuses**: Reducer types, contratos públicos de catálogo/carteira.
**Requirement**: SEL-01, SEL-02, SEL-03, SEL-04, SEL-05, SEL-08, SEL-10.
**What**: Centralizar totais, expansão de ocorrências, limites/elegibilidade e problemas de revisão sem alterar saldo.
**Done when**: [x] Caso 5000/1890/2090 produz 3980/1020; qty e totais seguros; erro/ausência de dados não vira zero financeiro válido; nenhuma regra duplicada em screen.
**Tests**: unit — teto de 12 casos: 0/1/10/11 ocorrências, duplicatas, saldo insuficiente, qty negativa/fracionária, código ausente, inativo, preço alterado e overflow; incluir duas lavagens 3780.
**Gate**: Q + CC.
**Commit**: `feat(checkout): derive bounded financial selection projections`

#### T13: manter seleção acima das abas
**Where**: `apps/mobile/src/features/checkout/CheckoutProvider.tsx`
**Depends on**: T12
**Reuses**: Reducer/seletores e queries públicas wallet/catalog; inserir provider no root.
**Requirement**: SEL-02, SEL-03, SEL-04, SEL-06, SEL-07.
**What**: Expor seleção e ações públicas validadas; persistir somente durante a sessão, sem saldo no Context.
**Done when**: [x] Adição valida dados/limites antes de dispatch; remoção continua possível; provider único acima das tabs; estado derivado não é segunda fonte de wallet/catalog.
**Tests**: integration — teto de 6 casos: seleção atravessa abas, insuficiência, wallet loading/erro, indisponibilidade, limite e limpeza preferência.
**Gate**: F + CC.
**Commit**: `feat(checkout): retain validated selection above tab navigation`

#### T14: selecionar serviços na Home
**Where**: `apps/mobile/src/features/home/HomeScreen.tsx`
**Depends on**: T13
**Reuses**: Contrato público do CheckoutProvider e ServiceCard, sem novos totais no JSX.
**Requirement**: SEL-01, SEL-02, SEL-03, SEL-04, SEL-05, UX-01.
**What**: Conectar adicionar/remover/quantidade/resumo selecionado e disponível, com CTA para revisão.
**Done when**: [x] Feedback de bloqueio é acessível mesmo com botão disabled; header não muda; nenhum POST; selecionar não controla máquina.
**Tests**: integration — teto de 6 casos: wash+dry 3980/1020 com header 5000, repetido, remover, insuficiência, wallet inválida e serviço indisponível.
**Gate**: F + CC.
**Commit**: `feat(home): select services without debiting the wallet`

#### T15: revisar seleção sem contratar prematuramente
**Where**: `apps/mobile/src/features/checkout/CheckoutScreen.tsx`
**Depends on**: T14
**Reuses**: Provider e seletores; screen introduzida no lugar do placeholder da rota checkout.
**Requirement**: SEL-07, SEL-08, SEL-09, SEL-10, BUY-14, UX-01.
**What**: Exibir quantidades, remover, preferência válida, total/estimativa distintos e aviso da futura cobrança imediata.
**Done when**: [x] Empty state volta à Home; preço alterado requer nova revisão; compra ainda não habilitada e isso é explícito ao usuário; sem rota de detalhe inexistente.
**Tests**: integration — teto de 7 casos: vazio/CTA, itens/total, preferência nula/válida, remoção preferida, mudança preço/elegibilidade, três rótulos financeiros e ausência de POST.
**Gate**: F + CC.
**Commit**: `feat(checkout): review quantities prices and execution preference`

#### T16: refletir quantidades no badge acessível
**Where**: `apps/mobile/src/app/(tabs)/_layout.tsx`
**Depends on**: T15
**Reuses**: Quantidade derivada pública do checkout; navegação da T5.
**Requirement**: NAV-01, NAV-02, SEL-06, SEL-11, UX-01.
**What**: Conectar badge e nome acessível sem recalcular a regra de quantidade no layout.
**Done when**: [x] Zero oculta badge; duas unidades do mesmo serviço contam 2; ordem/inicial das tabs e seleção preservadas; E2 demonstra seleção real sem débito.
**Tests**: integration — teto de 4 casos: zero, múltiplas ocorrências, remoção e navegação completa mantendo seleção/header.
**Gate**: F + B no fechamento E2 + C1 + CC.
**Commit**: `feat(navigation): expose accessible checkout item counts`

### Phase 5: E3, contratação segura na sessão

#### T17: consumir o POST financeiro com contrato próprio
**Where**: `apps/mobile/src/features/checkout/createBooking.ts`
**Depends on**: T16
**Reuses**: client, Zod e tipos OpenAPI reais quando disponíveis.
**Requirement**: BUY-01, BUY-04, SAFE-01, SAFE-02.
**What**: Serializar body/header definidos e validar resposta de criação/replay separadamente de futuros GETs.
**Done when**: [x] Request exato suporta duplicatas e preferência; 200/201 têm parsing validado; código de erro real registrado; temporários explicitados se contrato ainda ausente, sem habilitar integração.
**Tests**: integration — teto de 6 casos: unitário/batch, duplicatas, preferência, header UUID/body/total, replay 200 e response inválido; POST não calcula preço autoritativo.
**Gate**: F + CC; C2 antes de T21.
**Commit**: `feat(checkout): consume the idempotent booking contract`

#### T18: garantir uma intenção e um envio inicial
**Where**: `apps/mobile/src/features/checkout/useBookingSubmission.ts`
**Depends on**: T17
**Reuses**: createBooking; instalar expo-crypto no primeiro uso; mutation Query sem retry/pausa implícita.
**Requirement**: BUY-01, BUY-02, BUY-03, BUY-04, BUY-08, BUY-13.
**What**: Criar key/body imutáveis uma vez, guarda síncrona e estados idle/submitting/confirmed/rejected/unknown.
**Done when**: [x] Duplo toque antes do render gera um fetch; erro posterior ao envio conserva intenção; substituto de UUID/tempo só no limite; sem geração de chave no render/transport.
**Tests**: integration — teto de 8 casos: dois toques mesmo tick, UUID único, payload imutável, 201, replay 200, recusa definitiva, timeout/5xx/parse unknown, conflito bloqueado.
**Gate**: F + CC.
**Tools**: Context7 Query networkMode e Expo Crypto; usar APIs compatíveis com versões resolvidas.
**Commit**: `feat(checkout): guard immutable booking submission intents`

#### T19: verificar resultado incerto e exigir novo consentimento
**Where**: `apps/mobile/src/features/checkout/useBookingSubmission.ts`
**Depends on**: T18
**Reuses**: Intenção original, parser de deadline e queries de wallet/catalog.
**Requirement**: BUY-02, BUY-08, BUY-09, BUY-10, BUY-11, BUY-12, BUY-13, SEL-08.
**What**: Implementar verify explícito, revisão PRICE_CHANGED, allowlist de recusa e controle de Retry-After sem fila automática.
**Done when**: [x] verify usa key/body byte a byte originais; catálogo posterior não os altera; consentimento novo gera nova chave somente após rejeição definitiva/revisão; conflito ou retenção vencida/desconhecida não libera compra nova.
**Tests**: integration — teto de 10 casos: rede/timeout/5xx/2xx inválido, replay mesmo body/chave após catálogo mudar, PRICE_CHANGED sem reenvio silencioso, saldo insuficiente real, 429 segundos/data, conflito e ausência de POST por reconexão automática.
**Gate**: F + CC.
**Commit**: `feat(checkout): reconcile uncertain outcomes with explicit consent`

#### T20: sincronizar confirmação sem regressão de cache
**Where**: `apps/mobile/src/features/checkout/applyConfirmedBooking.ts`
**Depends on**: T19
**Reuses**: QueryClient e key pública wallet; prefixo público bookings definido junto ao primeiro consumidor de invalidação e reutilizado em T23/T25.
**Requirement**: BUY-05, BUY-07, SAFE-01.
**What**: Cancelar GET em voo antes de setar saldo confirmado compatível e invalidar queries pertinentes sem inventar metadados.
**Done when**: [x] GET antigo não derruba 1020; updatedAt não é inventado; response POST não é inserido como detalhe sem compatibilidade; cache failure não reenvia compra confirmada.
**Tests**: integration — teto de 6 casos: resposta antiga diferida após cancel, saldo confirmado 1020, wallet ausente, schema metadata preservada/refetch, invalidation seletiva, erro de refetch mantendo confirmed.
**Gate**: F + CC; política de consistência real registrada em C2.
**Commit**: `feat(checkout): apply confirmed balances without stale query races`

#### T21: conectar contratação e recuperação à revisão
**Where**: `apps/mobile/src/features/checkout/CheckoutScreen.tsx`
**Depends on**: T20
**Reuses**: Coordenador montado uma vez no provider acima das tabs, cache helper e seleção existente.
**Requirement**: BUY-01, BUY-02, BUY-03, BUY-04, BUY-05, BUY-06, BUY-07, BUY-08, BUY-09, BUY-10, BUY-11, BUY-12, BUY-13, BUY-14, UX-01.
**What**: Habilitar CTA só após C2 e conectar revisão/pending/unknown/rejected/success a comandos públicos, sem lógica financeira no JSX.
**Done when**: [~] C2 comprovado; seleção só limpa em sucesso; estado sobrevive troca de tabs; sucesso tem ID/itens/saldo e CTA a Serviços; demo compra 5000→3110 e replay sem segundo débito, sem reset de saldo não autorizado.
**Tests**: integration — teto de 10 casos de fluxo com providers reais: sucesso, replay, duplo toque, recusa, PRICE_CHANGED/revisão, unknown/verificar, rate limit, navegação durante pending, seleção preservada em erro e limpeza em confirmed.
**Gate**: F + B no fechamento E3 + C2 + CC.
**Commit**: `feat(checkout): expose safe purchase and recovery interactions`

### Phase 6: E4, histórico e recuperação de leitura

#### T22: centralizar classificação de status de bookings
**Where**: `apps/mobile/src/features/bookings/status.ts`
**Depends on**: T21
**Reuses**: Status da API/contrato; sem importar catálogo ou checkout.
**Requirement**: BOOK-02, BOOK-03, BOOK-05, SAFE-01.
**What**: Definir estados conhecidos e seletores de booking ativo/terminal, preservando lotes mistos.
**Done when**: [x] Algum item não terminal mantém booking ativo; todos terminais vão ao histórico; status desconhecido não é terminal nem habilita ação financeira.
**Tests**: unit — teto de 5 casos: cada não terminal parametrizado, todos terminais, misto, desconhecido e coleção malformada rejeitada na fronteira.
**Gate**: Q + CC.
**Commit**: `feat(bookings): centralize active and historical status rules`

#### T23: consultar listagem real de contratações
**Where**: `apps/mobile/src/features/bookings/listQuery.ts`
**Depends on**: T22
**Reuses**: Prefixo de bookings da T20, HTTP e schema do GET real, não do POST.
**Requirement**: BOOK-01, BOOK-02, BOOK-03, BOOK-07, SAFE-01.
**What**: Implementar list query e parâmetros/paginação efetivos, atualizando registro de contrato.
**Done when**: [~] Schemas e tipos GET corretos; key inclui parâmetros reais; ordem e alcance de páginas definidos; endpoint ausente não vira lista fake vazia.
**Tests**: integration — teto de 5 casos: registros reais de fixture, ordem por data, página/limite conforme contrato, response inválido, erro/endpoint ausente; zero rede real em Jest.
**Gate**: F + CC; C3 para saída E4.
**Commit**: `feat(bookings): query authoritative booking lists`

#### T24: apresentar Serviços com filtros e empty states
**Where**: `apps/mobile/src/features/bookings/BookingsScreen.tsx`
**Depends on**: T23
**Reuses**: listQuery, status e tokens; rota services compõe a screen.
**Requirement**: BOOK-01, BOOK-02, BOOK-03, BOOK-04, BOOK-07, UX-01.
**What**: Listar com FlatList/key estável, filtros locais, loading/error/retry e CTA Home quando vazio.
**Done when**: [~] Lote misto não perde itens concluídos no resumo; filtros não fingem completude paginada; data/ID/valor histórico/status legíveis; sem comandos P1 ainda.
**Tests**: integration — teto de 6 casos: loading, erro/retry, dois empty states, misto ativo, todos terminais e data decrescente; link usa bookingId real.
**Gate**: F + CC.
**Commit**: `feat(bookings): present active services and history filters`

#### T25: consultar detalhe com UUID validado
**Where**: `apps/mobile/src/features/bookings/detailQuery.ts`
**Depends on**: T24
**Reuses**: HTTP, prefixo público bookings, schema específico do GET detalhe.
**Requirement**: BOOK-05, BOOK-06, SAFE-01.
**What**: Validar ID antes de consultar e expor query de detalhe com preço histórico/status por item.
**Done when**: [~] ID inválido não envia GET; inexistente é recuperável; response é do contrato real e não reinterpretação de DTO POST; machine/queue/expiresAt só da API.
**Tests**: integration — teto de 5 casos: válido, inválido sem fetch, 404, DTO inválido, preço histórico diferente do catálogo.
**Gate**: F + CC; C3 para saída E4.
**Commit**: `feat(bookings): query validated booking details`

#### T26: exibir detalhe recuperável fora das tabs
**Where**: `apps/mobile/src/features/bookings/BookingDetailsScreen.tsx`
**Depends on**: T25
**Reuses**: detailQuery/status; rota fina bookings/[id] e retorno explícito.
**Requirement**: BOOK-05, BOOK-06, BOOK-07, UX-01.
**What**: Compor itens, preço contratado, status/fila/oferta informativos e erros, sem controles de endpoint ausente.
**Done when**: [~] Retorno para Serviços funciona; seleção não é apagada; cancelled/failed não implicam texto de refund sem campo autoritativo; máquinas identificadas como simuladas.
**Tests**: integration — teto de 6 casos: pronto, loading, UUID inválido/404, retry, preço histórico, estados e ausência de ações P1; retorno preserva seleção.
**Gate**: F + CC.
**Commit**: `feat(bookings): display authoritative booking details`

#### T27: recuperar foco e rede sem retomar comandos financeiros
**Where**: `apps/mobile/src/shared/hooks/useMobileLifecycle.ts`
**Depends on**: T26
**Reuses**: AppState, focusManager/onlineManager e expo-network instalado no primeiro uso; integração única no CheckoutProvider.
**Requirement**: SAFE-03, SAFE-04, SAFE-05, SEL-03, BUY-02, BUY-09.
**What**: Observar lifecycle/rede, limpar listeners e exigir revalidação de wallet/catalog antes de permitir seleção/compra após reconectar.
**Done when**: [x] undefined inicial não autoriza operação; header antigo é rotulado; reconexão/foreground refaz GETs e bloqueia compra até sucesso; zero POST sem gesto; subscriptions não duplicam.
**Tests**: integration — teto de 8 casos: rede inicial indeterminada, offline, reconnect com GET diferido, erro de refetch, active/background, unmount, rerender sem duplicação, unknown continua exigindo verify explícito.
**Gate**: F + CC.
**Tools**: Context7 expo-network/Query lifecycle; sem instalar NetInfo também.
**Commit**: `feat(mobile): revalidate reads across connectivity and focus changes`

#### T28: encaminhar confirmação para acompanhamento existente
**Where**: `apps/mobile/src/features/checkout/CheckoutScreen.tsx`
**Depends on**: T27
**Reuses**: Resultado confirmado e rota detalhe criada em T26; queries públicas, sem detalhes internos de bookings.
**Requirement**: BUY-06, BUY-07, BOOK-01, BOOK-05, SEL-06.
**What**: Conectar CTA pós-compra ao detalhe real e validar convergência da lista/detalhe após contratação.
**Done when**: [~] Nenhuma rota inexistente; booking confirmado aparece após invalidação/refetch; retornar à Home mantém novo saldo confirmado; C3/demo E4 atendidos.
**Tests**: integration — teto de 4 casos: compra→detalhe, compra→Serviços/lista, retorno às tabs e falha GET pós-sucesso sem POST repetido.
**Gate**: F + B no fechamento E4 + C3 + CC.
**Commit**: `feat(checkout): link confirmed purchases to live tracking`

### Phase 7: E5, fechamento P0 verificável

#### T29: registrar validação Android e acessibilidade real
**Where**: `apps/mobile/docs/native-validation.md`
**Depends on**: T28
**Reuses**: App integrado, API demo combinada e testes existentes.
**Requirement**: UX-01, UX-02, QUAL-02, SAFE-03, SAFE-04.
**What**: Preparar o checklist de smoke Android; o **usuário executa** e eu documento os resultados que ele reportar por cenário, sem E2E automatizado ou PASS inferido de export.
**Done when**: [~] N1 executado pelo usuário e aprovado; registrar dispositivo/API/commit/data, fonte 200%, leitor de tela, gestos/safe areas, navegação e falhas de rede/background; ausência de aparelho é BLOCKED.
**Tests**: manual — teto de 8 cenários: tabs/Home inicial; header/layout; wash 5000→3110 em cenário controlado; seleção/badge; histórico/detalhe; rede e retry explícito; suspensão/retomada; a11y/fonte ampliada. Cuidado com consumo de saldo em demo.
**Gate**: N1 + B + D + CC.
**Commit**: `docs(mobile): record verified android and accessibility evidence`

Correções descobertas em T29 tornam-se tarefas corretivas atômicas identificadas pelo arquivo/AC e com teste de regressão. A pré-aprovação cobre correções necessárias ao escopo, não uma tarefa genérica de refactor. Não marcar T29 concluída antes de corrigir e reexecutar os cenários falhos. Se não houver falhas, não criar commit de código vazio.

#### T30: fechar onboarding e evidência P0
**Where**: `apps/mobile/README.md`
**Depends on**: T29
**Reuses**: Baseline, registro contratual, evidência nativa e README raiz preservado.
**Requirement**: QUAL-01, QUAL-02; fechamento dos 50 requisitos P0.
**What**: Documentar instalação/ambientes/fluxo/estado/idempotência/limites e linkar pela raiz, incluindo resultados reais dos gates e limitações P1/P2.
**Done when**: [~] Onboarding reproduzido; comandos/URL por dispositivo corretos; recovery entre reinícios não é prometido; W e B registrados; todos P0 têm prova; após commit, disparar Verifier/sensor V sem perguntar se deve validar.
**Tests**: manual — teto de 6 conferências: instalação/execução, exemplos de URL não secretos, comandos de qualidade, confirmação vs pré-reserva, idempotência/limite de reinício, mapa de capacidades/limitações; teste bootstrap de documentação preservado.
**Gate**: D + B + W + N1 + CC; V é fechamento automático após commit.
**Commit**: `docs(mobile): publish reproducible p0 setup and evidence`

### Phase 8: E6, ciclo de serviços P1 condicional

Nenhuma tarefa desta fase começa antes de CP1. Cada comando abaixo requer seu próprio suporte comprovado; recurso ausente é **não aplicável, com evidência**, nunca implementado com endpoint inventado. Tarefa não aplicável não recebe commit vazio e não pode ser marcada como comportamento verificado. Seu predecessor de liberação é considerado encerrado após registrar formalmente N/A e a evidência de ausência de suporte, permitindo seguir para outra capacidade P1 independente; isso não autoriza dependência técnica inexistente. Countdown/confirm só avançam se seus próprios dados/endpoint existirem.

#### T31: ativar item pago pendente
**Where**: `apps/mobile/src/features/bookings/activateItem.ts`
**Depends on**: T30
**Reuses**: client/detailQuery/status; estender seletor de ação existente e integrar CTA mínimo ao detalhe.
**Requirement**: QUEUE-01, QUEUE-02, QUEUE-05, UX-01.
**What**: Implementar o único endpoint activate, mutation sem retry/pausa e disponibilidade pelo status/capacidade.
**Done when**: [ ] Só pending_activation oferece CTA; resultado é consultado/invalida detalhe/lista; queued explica pago; 409 faz refetch; ausência de suporte oculta ação.
**Tests**: integration — teto de 5 casos: sem capacidade, pending_activation, resposta queued/oferta, estado indevido sem POST, 409 e ação pending não duplicada.
**Gate**: CP1 + F + CC.
**Commit**: `feat(bookings): activate paid pending service items`

#### T32: renderizar contagem por prazo do servidor
**Where**: `apps/mobile/src/features/bookings/components/OfferCountdown.tsx`
**Depends on**: T31
**Reuses**: expiresAt do detalhe, Date.now e callback de refetch fornecido pelo consumidor.
**Requirement**: QUEUE-03, QUEUE-04, SAFE-05, UX-01.
**What**: Exibir restante calculado por timestamps, disparar expiração uma vez por oferta e limpar timer.
**Done when**: [ ] 60/1/0s corretos; foreground recalcula; não muda status; não anuncia cada segundo ao leitor de tela; timer inexistente depois de unmount.
**Tests**: integration — teto de 6 casos com clock fake: 60s, 1s, zero, timestamp já vencido, background/retorno e cleanup/rerender; callback de expiração não se repete no zero.
**Gate**: CP1 + F + CC.
**Commit**: `feat(bookings): render server-based offer countdowns`

#### T33: confirmar oferta elegível
**Where**: `apps/mobile/src/features/bookings/confirmItem.ts`
**Depends on**: T32
**Reuses**: client, status, OfferCountdown e detalhe existente; uma ação de domínio.
**Requirement**: QUEUE-01, QUEUE-03, QUEUE-04, QUEUE-05, UX-01.
**What**: Implementar confirm com bloqueio visual na expiração e submissão pendente, respeitando 410 mesmo com tempo local restante.
**Done when**: [ ] Só oferta atual habilita; zero não envia; 409/410 refaz leitura e informa estado real; sem transição local presumida para in_progress.
**Tests**: integration — teto de 5 casos: sucesso, zero sem POST, 410 antes do zero local, 409, duplo toque e retorno do background após expirar.
**Gate**: CP1 + F + CC.
**Commit**: `feat(bookings): confirm eligible machine offers`

#### T34: cancelar somente antes do início e refletir refund confirmado
**Where**: `apps/mobile/src/features/bookings/cancelItem.ts`
**Depends on**: T33
**Reuses**: status e client; sincronização de wallet por contrato público, sem depender de helper interno checkout.
**Requirement**: QUEUE-05, QUEUE-06, QUEUE-07, UX-01.
**What**: Implementar cancel com elegibilidade central e invalidação financeira somente após resposta autoritativa.
**Done when**: [ ] Ação ausente em in_progress/terminal; refund não aparece sem campo/prova real; sem crédito otimista; 409 refaz detalhe.
**Tests**: integration — teto de 6 casos: três estados permitidos parametrizados, in_progress, terminal, 409, sucesso com refund, resposta sem prova de refund e erro sem crédito.
**Gate**: CP1 + F + CC.
**Commit**: `feat(bookings): cancel eligible items with confirmed refund feedback`

#### T35: reportar problema sem prometer estorno
**Where**: `apps/mobile/src/features/bookings/reportIssue.ts`
**Depends on**: T34
**Reuses**: client, contrato real de report-issue, detalhe/status.
**Requirement**: QUEUE-08, QUEUE-07, UX-01.
**What**: Implementar report endpoint com feedback de verificação e sem alterar saldo.
**Done when**: [ ] 202 ou sucesso contratual diz Relato enviado para verificação; ausência de suporte oculta ação; nunca adiciona painel de operador.
**Tests**: integration — teto de 4 casos: aceito sem crédito, rejeitado/409, capacidade ausente, pending impede repetição; não apresentar texto de estorno.
**Gate**: CP1 + F + CC.
**Commit**: `feat(bookings): report incidents without implying refunds`

#### T36: atualizar apenas serviços visíveis e não terminais
**Where**: `apps/mobile/src/features/bookings/useBookingPolling.ts`
**Depends on**: T35
**Reuses**: Query list/detail, status e sinais lifecycle, sem segundo monitor de rede.
**Requirement**: QUEUE-02, QUEUE-09, SAFE-04, SAFE-05.
**What**: Aplicar polling condicional de 10s na lista e 5s no detalhe, ajustado ao rate limit real.
**Done when**: [ ] Blur/background/offline/terminal suspendem; foreground/reconexão refazem GET; não há polling de wallet por reflexo nem POST automático; cleanup sem observer duplicado.
**Tests**: integration — teto de 7 casos: lista ativa, detalhe ativo, blur, background, offline, todos terminais e reconnect; avançar clock para comprovar chamadas e suspensão reais.
**Gate**: CP1 + F + CC.
**Commit**: `feat(bookings): poll visible nonterminal service states`

#### T37: destacar ofertas disponíveis na lista Serviços
**Where**: `apps/mobile/src/features/bookings/BookingsScreen.tsx`
**Depends on**: T36
**Reuses**: listQuery, status, OfferCountdown e detalhe; sem duplicar políticas de confirmação.
**Requirement**: QUEUE-02, QUEUE-03, QUEUE-05, QUEUE-07, QUEUE-10, UX-01.
**What**: Destacar oferta/prazo e caminho claro ao detalhe; fechar demonstração P1 sem depender de SignalR.
**Done when**: [ ] Usuário vê oferta sem adivinhar detalhe; fila/estorno são autoritativos; smoke de activate→queued/oferta→confirm/expire/cancel/report real conforme suporte; relatório de capacidades atualizado; depois do commit iniciar V P1.
**Tests**: integration — teto de 5 casos: oferta destacada, lista sem oferta, expiração/refetch, lote misto e navegação; smoke manual real registra 60s/410 e estorno somente confirmado.
**Gate**: CP1 + F + B + W + demonstração P1 + CC; V fecha fase após commit.
**Commit**: `feat(bookings): surface actionable offers in service tracking`

## Diagram-Definition Cross-Check

O diagrama global contém exatamente o predecessor imediato de cada tarefa. Não há dependência futura; entradas externas são gates nomeados, não falsas setas para tarefas inexistentes.

| Task | Depends On (corpo) | Diagram Shows | Status |
| --- | --- | --- | --- |
| T1 | None | Sem entrada | Match |
| T2 | T1 | T1 → T2 | Match |
| T3 | T2 | T2 → T3 | Match |
| T4 | T3 | T3 → T4 | Match |
| T5 | T4 | T4 → T5 | Match |
| T6 | T5 | T5 → T6 | Match |
| T7 | T6 | T6 → T7 | Match |
| T8 | T7 | T7 → T8 | Match |
| T9 | T8 | T8 → T9 | Match |
| T10 | T9 | T9 → T10 | Match |
| T11 | T10 | T10 → T11 | Match |
| T12 | T11 | T11 → T12 | Match |
| T13 | T12 | T12 → T13 | Match |
| T14 | T13 | T13 → T14 | Match |
| T15 | T14 | T14 → T15 | Match |
| T16 | T15 | T15 → T16 | Match |
| T17 | T16 | T16 → T17 | Match |
| T18 | T17 | T17 → T18 | Match |
| T19 | T18 | T18 → T19 | Match |
| T20 | T19 | T19 → T20 | Match |
| T21 | T20 | T20 → T21 | Match |
| T22 | T21 | T21 → T22 | Match |
| T23 | T22 | T22 → T23 | Match |
| T24 | T23 | T23 → T24 | Match |
| T25 | T24 | T24 → T25 | Match |
| T26 | T25 | T25 → T26 | Match |
| T27 | T26 | T26 → T27 | Match |
| T28 | T27 | T27 → T28 | Match |
| T29 | T28 | T28 → T29 | Match |
| T30 | T29 | T29 → T30 | Match |
| T31 | T30 | T30 → T31, mais CP1 | Match |
| T32 | T31 | T31 → T32 | Match |
| T33 | T32 | T32 → T33 | Match |
| T34 | T33 | T33 → T34 | Match |
| T35 | T34 | T34 → T35 | Match |
| T36 | T35 | T35 → T36 | Match |
| T37 | T36 | T36 → T37 | Match |

## Test Co-location Validation

| Task | Camada | Matrix Requires | Task Says | Status |
| --- | --- | --- | --- | --- |
| T1 | Baseline documental | manual | manual + suíte herdada | OK |
| T2 | Config runtime | unit | unit | OK |
| T3 | Erros runtime | unit | unit | OK |
| T4 | Transporte | unit | unit | OK |
| T5 | Router/config integração | integration | integration | OK |
| T6 | Endpoint/query | integration | integration | OK |
| T7 | UI/helper | integration + unit do helper | integration + unit no mesmo commit | OK |
| T8 | Endpoint/query | integration | integration | OK |
| T9 | UI | integration | integration | OK |
| T10 | Screen | integration | integration | OK |
| T11 | Reducer | unit | unit | OK |
| T12 | Seletores | unit | unit | OK |
| T13 | Provider | integration | integration | OK |
| T14 | Screen | integration | integration | OK |
| T15 | Screen | integration | integration | OK |
| T16 | Navegação | integration | integration | OK |
| T17 | Endpoint | integration | integration | OK |
| T18 | Submissão | integration | integration | OK |
| T19 | Recovery | integration | integration | OK |
| T20 | Cache | integration | integration | OK |
| T21 | Fluxo financeiro | integration | integration | OK |
| T22 | Status | unit | unit | OK |
| T23 | Endpoint/query | integration | integration | OK |
| T24 | Screen | integration | integration | OK |
| T25 | Endpoint/query | integration | integration | OK |
| T26 | Screen/rota | integration | integration | OK |
| T27 | Lifecycle | integration | integration | OK |
| T28 | Navegação/fluxo | integration | integration | OK |
| T29 | Native/evidência | manual | manual + suíte herdada | OK |
| T30 | Onboarding/evidência | manual | manual + suíte herdada | OK |
| T31 | Endpoint/ação | integration | integration | OK |
| T32 | Tempo/UI | integration | integration | OK |
| T33 | Endpoint/ação | integration | integration | OK |
| T34 | Endpoint/ação | integration | integration | OK |
| T35 | Endpoint/ação | integration | integration | OK |
| T36 | Polling | integration | integration | OK |
| T37 | Screen/tempo | integration | integration + manual | OK |

## Task Granularity Check

- T1, T29, T30: uma entrega documental verificável cada; não encobrem implementação de features.
- T2–T4, T6, T8, T11–T13, T17–T20, T22–T23, T25, T27, T31–T36: um módulo/regra, um endpoint ou uma política por tarefa, com teste no mesmo commit.
- T7, T9–T10, T14–T16, T21, T24, T26, T28, T37: um componente ou uma integração de interação por tarefa, com glue mínimo em seu consumidor, sem postergar teste.
- T5: exceção justificada de migração de entrypoint/Router, unidade indivisível de configuração/composição com gate B.
- Dependências, manifests e lock mudam junto ao primeiro consumidor. Não criar tarefas apenas para instalar pacotes sem comportamento testável.
- Nenhuma fase excede 7 tarefas; E1 dividida em duas fases coesas. Máximo de uma screen com regra própria por tarefa; rotas finas não duplicam screens.

## Requirement → Task → Proof

Os testes são criados nas tarefas indicadas, e os paths/resultados reais serão registrados em execução. O mapeamento abaixo cobre cada um dos 60 IDs da spec; não confundir destino de prova com critério já verificado.

| Requirement ID | Tarefas responsáveis | Prova esperada |
| --- | --- | --- |
| NAV-01 | T5, T16 | Ordem acessível das tabs |
| NAV-02 | T5, T16 | Home inicial |
| NAV-03 | T2, T7 | Nome configurado/fallback |
| READ-01 | T6, T7 | Saldo API em BRL |
| READ-02 | T6, T7 | Loading sem moeda fictícia |
| READ-03 | T6, T7 | Erro/retry sem zero falso |
| READ-04 | T8, T9, T10 | Preços/nomes da API |
| READ-05 | T8, T10 | Empty/error/retry |
| READ-06 | T8, T9, T10, T14 | Elegibilidade e motivo |
| SEL-01 | T12, T14 | 5000/3980/1020 sem débito |
| SEL-02 | T12, T13, T14 | Seleção não cresce sem saldo |
| SEL-03 | T12, T13, T14, T27 | Sem dados/revalidação, sem compra |
| SEL-04 | T12, T13, T14 | 10 permitido, 11 bloqueado |
| SEL-05 | T11, T12, T14 | Duplicatas preservadas |
| SEL-06 | T13, T16, T28 | Estado preservado nas tabs |
| SEL-07 | T11, T13, T15 | Preferência limpa ao remover |
| SEL-08 | T12, T15, T19 | Mudança exige revisão |
| SEL-09 | T11, T15 | Vazio sem POST e CTA |
| SEL-10 | T12, T15 | Resumo com três valores distintos |
| SEL-11 | T16 | Badge soma quantidades |
| BUY-01 | T4, T17, T18, T21 | Header/body/UUID exatos |
| BUY-02 | T18, T19, T21, T27 | Uma intenção pendente |
| BUY-03 | T18, T21 | Um envio por duplo toque |
| BUY-04 | T17, T18, T21 | 201/200 mesmo booking |
| BUY-05 | T20, T21 | GET antigo não sobrescreve |
| BUY-06 | T21, T28 | Limpa após confirmar, CTA correto |
| BUY-07 | T20, T21, T28 | Cache/refetch seletivos |
| BUY-08 | T4, T18, T19, T21 | Unknown em falhas pós-envio |
| BUY-09 | T19, T21, T27 | Mesma chave/body, gesto explícito |
| BUY-10 | T19, T21 | PRICE_CHANGED e novo consentimento |
| BUY-11 | T19, T21 | Recusa sem cobrança presumida |
| BUY-12 | T3, T19, T21 | Deadline e nenhum loop |
| BUY-13 | T3, T18, T19, T21 | Conflito não troca chave |
| BUY-14 | T15, T21 | Aviso imediato/simulado |
| BOOK-01 | T23, T24, T28 | Lista real ordenada |
| BOOK-02 | T22, T23, T24 | Lote misto ativo |
| BOOK-03 | T22, T23, T24 | Todos terminais no histórico |
| BOOK-04 | T24 | Dois vazios distintos |
| BOOK-05 | T22, T25, T26, T28 | Preço e estado históricos |
| BOOK-06 | T25, T26 | Inválido sem GET; 404 recuperável |
| BOOK-07 | T5, T10, T23, T24, T26 | Limitação honesta |
| SAFE-01 | T3, T4, T6, T8, T17, T20, T22, T23, T25 | Zod/contrato/inteiro seguro |
| SAFE-02 | T2, T3, T4, T17 | Erro por código/status |
| SAFE-03 | T27, T29 | Offline bloqueia e rotula |
| SAFE-04 | T27, T29, T36 | Refetch sem POST automático |
| SAFE-05 | T27, T32, T36 | Cleanup sem multiplicação |
| UX-01 | T5, T7, T9, T10, T14, T15, T16, T21, T24, T26, T29, T31, T32, T33, T34, T35, T37 | Roles/labels/estados e feedback |
| UX-02 | T5, T7, T29 | Safe areas/fonte 200% Android |
| QUAL-01 | T2, T30 | README reproduzível |
| QUAL-02 | T1, T29, T30 | Gates e Android reais |
| QUEUE-01 | T31, T33 | Ações por estado/capacidade |
| QUEUE-02 | T31, T36, T37 | Fila autoritativa e pago |
| QUEUE-03 | T32, T33, T37 | 60/1/0 por timestamp |
| QUEUE-04 | T32, T33 | Zero bloqueia, não transiciona |
| QUEUE-05 | T31, T33, T34, T37 | 409/410 reconsultam |
| QUEUE-06 | T34 | Sem cancel em execução |
| QUEUE-07 | T34, T35, T37 | Refund somente confirmado |
| QUEUE-08 | T35 | Relato não é estorno |
| QUEUE-09 | T36 | Polling condicional com cleanup |
| QUEUE-10 | T37 | Oferta visível em Serviços |

## Fechamento independente obrigatório

Depois de T30, antes de E6, e depois de T37, executar Verifier independente da autoria. Re-derivar oráculos a partir da spec, não aceitar número de testes como prova. Relatório `validation.md` contém escopo P0/P1 efetivamente verificado, AC→file:line, faixa Git, comandos/contagens reais e verdict PASS/FAIL. Não declarar P1 entregue por um PASS apenas de P0.

Sensor em scratch isolado, sem stash e sem mutação na árvore real: testar pelo menos nova chave no replay, débito local na seleção, POST sem revisão de preço, GET antigo sobrescrevendo saldo; em P1 acrescentar confirmação vencida/cancel após início/polling em background. Mutantes sobreviventes geram tarefas de correção com prova, até 3 rodadas antes de escalar. Registrar porcelain antes/depois; deve ser idêntico na árvore real.

Relatório pode ser versionado em commit documental de fechamento depois do último commit funcional, sem misturar código ou declarar tarefas anteriores novamente concluídas. Executar CC e validate_state para o fechamento. Lessons só são registradas para falha fundamentada, nunca inventadas num PASS limpo.

## Estado inicial e retomada

- Planejamento: pré-aprovado por autorização expressa. Estrutura de spec/tasks deve passar antes de Execute.
- Implementação: **21/37 tarefas com Done when marcado** (última: T30); demais abertas ou pendentes de gate externo.
- Revisão 2026-10-21: testes mínimos e escopados (AD-004) e validação Android pelo usuário (AD-005) incorporados; tetos de casos em `Tests` não são obrigação.
- Integrações externas: C1/C2/C3/CP1 não comprovadas; N1 não executado; não inferir endpoints do handoff.
- P2: fora da lista padrão; ativação exige decisão explícita e ACs/tarefas próprios, conforme spec aprovada.
- Próxima ação quando o usuário pedir implementar: reconciliar Git/STATE, ler implement.md, validar documentos, confirmar ferramentas/oferecer lotes e começar T1. Não abrir Plannotator por rotina.
