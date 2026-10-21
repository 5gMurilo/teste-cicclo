# Cicclo mobile: validação independente do escopo P0

**Data:** 2026-10-21
**Criado:** 2026-10-21. **Última atualização:** 2026-10-21 (iteração 2 de 3).
**Spec:** `.specs/features/cicclo-mobile/spec.md`
**Faixa Git:** `08e6c7d..HEAD`. Iteração 1 verificou `e16246c`; iteração 2 verificou `db1ea75` (`fix(wallet): label the header balance and close verifier test gaps`, 13 arquivos, só `AppHeader.tsx` em produção).
**Verifier:** agente independente (autor diferente do verificador). Nenhum código de produção ou teste foi alterado na árvore real; este arquivo é a única escrita.

Verdict: PASS

**Result**: PASS

Condições do PASS: todo AC P0 testável sem a API real tem evidência `file:linha` com asserção sobre o resultado da spec, nenhum mutante sobreviveu na re-verificação (28 de 28 mortos, incluindo os 8 sobreviventes da iteração 1) e os gates passam. Os itens da seção "Pendente por dependência externa" não foram aprovados por este relatório e, isoladamente, não forçam FAIL. Este PASS não declara a entrega P0 concluída: ela continua dependendo de C1/C2/C3 e de N1.

---

## Escopo

**Verificado (50 IDs P0):** NAV-01..03, READ-01..06, SEL-01..11, BUY-01..14, BOOK-01..07, SAFE-01..05, UX-01..02, QUAL-01..02.

**Fora do escopo desta verificação (não é falha):** QUEUE-01..QUEUE-10 (P1, tarefas T31-T37 deliberadamente não implementadas).

## Pendente por dependência externa (não aprovado aqui)

| Item | Situação |
| --- | --- |
| Gates C1, C2, C3 contra a API real | Nada foi executado contra a API real. Compra real desligada por `capabilities.purchase = false` (`apps/mobile/src/shared/api/capabilities.ts:7`); os testes a ligam com `jest.mock` |
| N1 (Android real, TalkBack, fonte ampliada, safe areas) | Pendente, executado pelo usuário (AD-005). `apps/mobile/docs/native-validation.md` é só um roteiro, "Estado: PENDENTE" |
| UX-02 | Só a parte automatizável (`AppHeader.test.tsx:53-56`) está coberta; o restante exige Android |
| QUAL-02, smoke Android | Pendente (N1) |
| UX-01, leitor de tela | Roles, labels e estados cobertos por RNTL; verificação com TalkBack pendente (N1) |
| Retenção da Idempotency-Key | Não provada (`keyRetentionMs = null`); gate C2 |
| `pnpm test:bootstrap` e `pnpm lint/test/build` do workspace | Falham só por falta de `dotnet`; não executados nem verificados por este Verifier |

---

## Tarefas (tasks.md)

T1-T30 com Done when `[x]` ou `[~]`. `[~]` = implementado e aguardando gate externo: T10, T21, T23-T26, T28, T29, T30. T31-T37 abertas (P1, fora de escopo).

---

## Comandos executados (iteração 2, árvore real, HEAD `db1ea75`)

| Comando | Resultado real |
| --- | --- |
| `pnpm --filter @cicclo/mobile typecheck` | exit 0 |
| `pnpm --filter @cicclo/mobile lint` (`--max-warnings 0`) | exit 0, zero avisos |
| `pnpm --filter @cicclo/mobile test` | exit 0: **30 suítes / 30 passaram; 252 testes / 252 passaram; 0 falhas; 0 ignorados** |
| `pnpm --filter @cicclo/mobile build` (`expo export`) | exit 0 na iteração 1 (`e16246c`, worktree descartável). Não reexecutado na iteração 2: a mudança de produção é só texto de rótulo em `AppHeader.tsx` |
| Grep `test.skip|it.skip|describe.skip|.only(|xit|xdescribe|test.todo|it.todo|TODO|FIXME|fit(|fdescribe` em `src`, `__tests__`, `jest.setup.ts` | Nenhuma ocorrência |

Contagem de testes: 1 antes da feature, 242 em `e16246c`, 252 em `db1ea75` (+10 testes novos, nenhum removido ou enfraquecido; só os 4 testes que citavam `Saldo:` foram atualizados para `Saldo da carteira:`). Remoção de `App.test.tsx` na faixa autorizada pela spec (linha 69), cobertura migrada.

---

## Critérios de aceite: spec, asserção e evidência

Caminhos relativos a `apps/mobile/`. Linhas valem para `db1ea75`.

### Navegação e leitura

| ID | Resultado esperado pela spec | Evidência `file:linha` + asserção | Resultado |
| --- | --- | --- | --- |
| NAV-01 | Ordem Serviços, Home, Checkout | `__tests__/navigation.test.tsx:17-19` `getByLabelText('Serviços, tab, 1 of 3')`, `'Home, tab, 2 of 3'`, `'Checkout, tab, 3 of 3'` | COVERED |
| NAV-02 | Abre na Home sem deep link | `navigation.test.tsx:16` `getPathname()` `toBe('/')`; `:20` `toBeSelected()` | COVERED |
| NAV-03 | `Olá! {displayName}` à esquerda; env ou `Visitante` | `__tests__/header.test.tsx:45` `getByRole('header', { name: 'Olá! Visitante' })`; `src/shared/config/__tests__/env.test.ts:5-8,16-20`; `AppHeader.test.tsx:15`. Posição "à esquerda" é estilo, não asserida | COVERED |
| READ-01 | `Saldo da carteira` à direita com `balanceCents` em BRL | `src/shared/layout/__tests__/AppHeader.test.tsx:16` `getByText('R$ 50,00')`, `:17` `getByText('Saldo da carteira')`, `:18` `getByLabelText('Saldo da carteira: R$ 50,00')`; `header.test.tsx:44`; fim a fim com 3110: `purchaseFlow.test.tsx:123`, `purchaseTracking.test.tsx:113`. Produção: `AppHeader.tsx:44-45`. Mutantes M32 e M33 (voltar ao rótulo antigo) mortos | COVERED (era GAP) |
| READ-02 | Loading sem valor monetário | `header.test.tsx:42` `getByText('Carregando saldo…')`; `AppHeader.test.tsx:24-25` `queryByText(/R\$/)` nulo | COVERED |
| READ-03 | Falha: saldo indisponível + retry, sem zero | `header.test.tsx:56-60`; `AppHeader.test.tsx:31-34`; mutante M10 (erro vira 0) morto na iteração 1 | COVERED |
| READ-04 | Nomes e preços da resposta | `__tests__/home.test.tsx:62-65` `getByText('R$ 25,00')` com preço servido 2500; `src/features/catalog/__tests__/queries.test.ts:51-54` | COVERED |
| READ-05 | Erro com retry; coleção vazia com estado vazio | `home.test.tsx:75` `'Nenhum serviço disponível no momento.'`; `:83-86` retry e recupera | COVERED |
| READ-06 | Indisponível bloqueia com motivo textual | `home.test.tsx:95` `'Serviço desativado'`; `homeSelection.test.tsx` botão `toBeDisabled()`; `ServiceCard.test.tsx:33-38,67-70` | COVERED |

### Seleção

| ID | Resultado esperado | Evidência | Resultado |
| --- | --- | --- | --- |
| SEL-01 | 3980 selecionado, 1020 disponível, header 5000 | `homeSelection.test.tsx:62-64` `Selecionado: R$ 39,80`, `Disponível para adicionar: R$ 10,20`, `Saldo da carteira: R$ 50,00`; `:65` só GET; `checkoutSelectors.test.ts:30-37`. M2, M2b mortos | COVERED |
| SEL-02 | Excede saldo: mantém seleção + mensagem | `CheckoutProvider.test.tsx:105-107` `{ok:false, reason:'insufficient-balance'}`, `quantities {wash:1}`; `homeSelection.test.tsx:94-96` `'Saldo insuficiente para adicionar este serviço.'`. M23 morto | COVERED |
| SEL-03 | Bloquear adições e contratação sem dados confiáveis ou sem confirmação após reconexão | Adições: `CheckoutProvider.test.tsx:119,131`; `homeSelection.test.tsx:99-109`; `lifecycle.test.tsx:96-158`. **Contratação (novo):** `lifecycle.test.tsx:164` `toBeEnabled()` com dados confiáveis, `:167` `toBeDisabled()` offline, `:174` `toBeDisabled()` durante a releitura pendente, `:177` volta a `toBeEnabled()`, `:178` `posts` igual a 0. M26 e M14 mortos | COVERED (era GAP) |
| SEL-04 | 10 permitido; 11ª impedida com explicação | `CheckoutProvider.test.tsx:157-159` `over-limit`; `checkoutSelectors.test.ts:61-64,136`; **UI (novo):** `homeSelection.test.tsx:122` `getByRole('alert')` `toHaveTextContent('Limite de 10 serviços por contratação.')`, `:123` seleção mantida em `R$ 10,00`; `:119` sem alerta até a 10ª. M31 e M11 mortos | COVERED (era GAP) |
| SEL-05 | Quantidade 2 vira duas entradas | `checkoutSelectors.test.ts:43`; `homeSelection.test.tsx:75`; `createBooking.test.ts:49`. M24 morto | COVERED |
| SEL-06 | Seleção preservada entre abas | `CheckoutProvider.test.tsx:86` `quantity 1`; `checkoutBadge.test.tsx:62-67` | COVERED |
| SEL-07 | Última ocorrência limpa a preferência | `checkoutReducer.test.ts:48-50`; `CheckoutProvider.test.tsx:195`; `checkoutReview.test.tsx:97-99`. M25 morto | COVERED |
| SEL-08 | Mudança de preço/elegibilidade bloqueia consentimento antigo até nova revisão | Preço: `checkoutReview.test.tsx:108-116`; `purchaseFlow.test.tsx:177-182` botão `toBeDisabled()` até `Aceitar novos preços`. **Elegibilidade e saldo (novo):** `purchaseFlow.test.tsx:267` botão `toBeDisabled()` com serviço desativado após a seleção, `:278` e `:280` `toBeDisabled()` quando o novo preço (6000) excede o saldo, antes e depois de `Aceitar novos preços`, `posts` 0. M3a e M27 mortos | COVERED (era GAP) |
| SEL-09 | Vazio: `Escolher serviços`, sem POST | `checkoutReview.test.tsx:67-71` | COVERED |
| SEL-10 | Três rótulos distintos | `checkoutReview.test.tsx:85-87`. M2b morto | COVERED |
| SEL-11 | Badge soma quantidades | `checkoutBadge.test.tsx:47,50,53` | COVERED |

### Contratação

| ID | Resultado esperado | Evidência | Resultado |
| --- | --- | --- | --- |
| BUY-01 | POST com `serviceCodes`, `expectedTotalCents`, preferência opcional, UUID v4 | `purchaseFlow.test.tsx:120-121` corpo `{serviceCodes:['wash'], expectedTotalCents:1890}` e regex UUID v4; `createBooking.test.ts:73-76`. **Preferência (novo):** `purchaseFlow.test.tsx:245` corpo igual a `{serviceCodes:['wash','dry'], expectedTotalCents:3980, preferredFirstServiceCode:'dry'}` após escolher o radio `Secagem`; `:256` sem preferência o corpo `not.toHaveProperty('preferredFirstServiceCode')`. M20 e M38 mortos | COVERED (era GAP) |
| BUY-02 | `submitting`/`unknown` impedem nova intenção | `useBookingSubmission.test.tsx:169-170`; `purchaseFlow.test.tsx:143-149,199`. M5, M5c mortos | COVERED |
| BUY-03 | Duplo toque: no máximo um envio, guarda no comando | `useBookingSubmission.test.tsx:79-86` `sent toHaveLength(1)`. M5, M5b mortos | COVERED |
| BUY-04 | 201 e replay 200: booking confirmado único | `useBookingSubmission.test.tsx:123-134`; `purchaseFlow.test.tsx:133-134` | COVERED |
| BUY-05 | Header usa `remainingBalanceCents`; GET anterior não sobrescreve | `applyConfirmedBooking.test.tsx:53` `balanceCents: 3110` após GET tardio; `purchaseFlow.test.tsx:123` `Saldo da carteira: R$ 31,10`. M4, M21 mortos | COVERED |
| BUY-06 | Limpa seleção e dá acesso à contratação | `purchaseFlow.test.tsx:124`; `purchaseTracking.test.tsx:97-100`. M17 morto | COVERED |
| BUY-07 | Invalidação seletiva | `applyConfirmedBooking.test.tsx:62,79-81`. M22 morto (iteração 1) | COVERED |
| BUY-08 | Timeout, rede, 5xx, sucesso inválido: `unknown`, chave e payload congelados | `useBookingSubmission.test.tsx:149-170` (5xx, rede, JSON inválido, corpo fora do contrato, 4xx fora da lista, 4xx sem ProblemDetails). **Timeout (novo):** `useBookingSubmission.test.tsx:201` `classifyFailure(new TransportError('timeout'))` `toEqual({ status: 'unknown', cause: 'timeout', retryAfterAt: null })`, `:202` rede -> `unknown`/`network`. A produção de `timeout` por `requestJson` está provada em `client.test.ts:142-155`. M28 e M15 mortos | COVERED (era GAP) |
| BUY-09 | Verificar reenvia mesma chave e mesmo payload | `useBookingRecovery.test.tsx:99-101`; `purchaseFlow.test.tsx:206-207`; `useBookingSubmission.test.tsx:118-120`. M1, M1b mortos | COVERED |
| BUY-10 | `PRICE_CHANGED`: novo consentimento, nova chave só na nova intenção | `useBookingRecovery.test.tsx:160-167`; `purchaseFlow.test.tsx:177-187`. M3a, M3b mortos | COVERED |
| BUY-11 | Saldo insuficiente: atualiza carteira, sem cobrança | `useBookingRecovery.test.tsx:177-178`; `purchaseFlow.test.tsx:161-162` | COVERED |
| BUY-12 | 429 bloqueia até `Retry-After`, sem loop | `useBookingRecovery.test.tsx:191-200,208,215`; `purchaseFlow.test.tsx:217`. M9 morto | COVERED |
| BUY-13 | `IDEMPOTENCY_CONFLICT` sem trocar chave | `useBookingSubmission.test.tsx:182-183`; `useBookingRecovery.test.tsx:134-135`; `purchaseFlow.test.tsx:227-229`. M13 morto | COVERED |
| BUY-14 | Aviso de cobrança imediata e simulação | `checkoutReview.test.tsx:134-135` | COVERED |

### Acompanhamento

| ID | Resultado esperado | Evidência | Resultado |
| --- | --- | --- | --- |
| BOOK-01 | Ordem `createdAt` decrescente, identidade estável, navega ao detalhe | `bookingsScreen.test.tsx:80-83`; `listQuery.test.ts:73-77`; `bookingsScreen.test.tsx:180`. **Desempate (novo):** `listQuery.test.ts:94` mesmos `createdAt`, `bookingId.slice(-1)` igual a `['3','2','1']`. M29 e M18 mortos | COVERED (era GAP parcial) |
| BOOK-02 | Lote misto continua em andamento | `bookingsScreen.test.tsx:99-101`; `status.test.ts:27`. M12 morto | COVERED |
| BOOK-03 | Histórico só com todos terminais | `bookingsScreen.test.tsx:103-105`; `status.test.ts:22-23` | COVERED |
| BOOK-04 | Estado vazio específico por filtro com CTA à Home | Em andamento: `bookingsScreen.test.tsx:112,118-119`. **Histórico (novo):** `:129` `getByText('Nenhuma contratação no histórico.')`, `:130` sem cartões, `:132` `Escolher serviços` leva a `'/'`. M30 morto | COVERED (era GAP) |
| BOOK-05 | Preço histórico e estado por item da API | `bookingDetails.test.tsx:79-86`; `detailQuery.test.ts:51-56` | COVERED |
| BOOK-06 | ID inválido ou inexistente: erro recuperável, sem GET | `bookingDetails.test.tsx:121-125,132-135`; `detailQuery.test.ts:60-69`. M8 morto | COVERED |
| BOOK-07 | Limitação honesta | `bookingsScreen.test.tsx:152-153`; `bookingDetails.test.tsx:96`; `listQuery.test.ts:143-145` | COVERED |

### Fronteiras e experiência

| ID | Resultado esperado | Evidência | Resultado |
| --- | --- | --- | --- |
| SAFE-01 | Zod sem fallback; inteiro seguro | `wallet/__tests__/queries.test.ts:48-62`; `catalog/__tests__/queries.test.ts:85-99`; `createBooking.test.ts:88-97`; `listQuery.test.ts:122-131`; `detailQuery.test.ts:82-90`; `money.test.ts:10-12` | COVERED |
| SAFE-02 | Erro por status e `errorCode`; corpo vazio/HTML/JSON inválido | `apiError.test.ts:20-41`; `client.test.ts:79-118` | COVERED |
| SAFE-03 | Offline: sem compra com saldo antigo; valor retido rotulado | Rótulo: `lifecycle.test.tsx:116`. Compra bloqueada (novo): `lifecycle.test.tsx:167` botão `toBeDisabled()` offline e `:178` `posts` 0. M26, M14, M45 mortos | COVERED (era GAP) |
| SAFE-04 | Foreground e reconexão refazem GET, sem POST | `lifecycle.test.tsx:189-191,133-140,209`; `useMobileLifecycle.test.tsx:96-106`. M7, M19, M46 mortos | COVERED |
| SAFE-05 | Listeners e timers removidos, sem multiplicar | `useMobileLifecycle.test.tsx:114-120,123-133`; `client.test.ts:157-167`. M16 morto. Timer de `Retry-After` em `CheckoutScreen.tsx:206-212` sem teste de cleanup (nota) | COVERED |
| UX-01 | Role, label e estado coerentes; feedback em texto | `ServiceCard.test.tsx:52,67`; `checkoutReview.test.tsx:92-99`; `bookingsScreen.test.tsx:103`; `purchaseFlow.test.tsx:178`; `homeSelection.test.tsx:94`; `ServiceCard.test.tsx:33-38` | COVERED na parte automatizável; leitor de tela PENDING-EXTERNAL |
| UX-02 | Fonte ampliada nas três abas, safe-area sem duplicação | Parcial: `AppHeader.test.tsx:53-56` | PENDING-EXTERNAL (N1) |
| QUAL-01 | README completo e fiel | `README.md:20-27,36-42,56-60,70-78,80-85,87-99`. Contagem atualizada para 252 testes (`README.md:95`), conferida contra a execução real | COVERED (inspeção documental) |
| QUAL-02 | Lint, typecheck, testes, export, smoke Android, sem testes ignorados | Gates da seção "Comandos executados": todos exit 0, 0 ignorados | COVERED nos gates automáticos; smoke Android PENDING-EXTERNAL |

**Total:** 48 de 50 IDs com evidência completa ou completa na parte automatizável; 0 GAP; UX-02 e o smoke Android de QUAL-02 pendentes de N1 (UX-01 nativo também). Antes: 39 completos e 9 com GAP.

---

## Lacunas de precisão da spec (inalteradas, não bloqueiam)

| Tema | Observação |
| --- | --- |
| NAV-03/READ-01, posição esquerda/direita | Estilo; fica para N1 |
| BUY-05 consistência de leitura | Política depende do gate C2; o testável (cancelar GET em voo antes de gravar) está coberto |
| BUY-07 "detalhe pertinente" | A implementação invalida todo o prefixo `bookings`; spec não define o recorte |
| BUY-11 código do contrato | Vem de `docs/api-contract.md` (leitura do backend), não da API real; fecha com C2 |
| UX-01 "não depender só de cor" | Só há prova de haver texto; contraste e leitor de tela em N1 |
| BOOK-01 "identidade estável" | Interpretada como desempate determinístico por `bookingId`, agora testado |
| READ-01 e estado "stale" | A variante desatualizada do saldo usa `Saldo desatualizado: ...` (`AppHeader.tsx:54`), não `Saldo da carteira`; a spec só fixa o rótulo do saldo confirmado |

---

## Sensor de discriminação

**Profundidade:** P0 completo. Iteração 1: 40 mutantes em `e16246c` (32 mortos, 8 sobreviveram). Iteração 2: reinjeção em worktree descartável novo em `db1ea75` (`git worktree add --detach`, `node_modules` por symlink, removido com `git worktree remove --force`). Cada mutante rodou `typecheck` (exit 0 em todos) e a suíte completa (252 testes). Árvore real nunca alterada.

### Iteração 2: os 8 sobreviventes da iteração 1 (todos mortos)

| Mutante | Arquivo | Resultado | Teste que matou |
| --- | --- | --- | --- |
| M26 `canConfirm` ignora `readiness` | `CheckoutProvider.tsx:167` | Morto | `lifecycle.test.tsx` "disables the purchase button offline and while the re-read is pending, with no POST" |
| M27 `canConfirm` ignora `derived.problems` | `CheckoutProvider.tsx:170` | Morto (2 falhas) | `purchaseFlow.test.tsx` "disables the purchase button when a selected service becomes unavailable"; "keeps the purchase button disabled when a price rise no longer fits the balance..." |
| M28 timeout vira `rejected` | `useBookingSubmission.ts` (`classifyFailure`) | Morto | `useBookingSubmission.test.tsx` "classifyFailure keeps a client timeout as unknown..." |
| M20 provider envia preferência `null` | `CheckoutProvider.tsx:201` | Morto | `purchaseFlow.test.tsx` "sends the chosen execution preference in the request body" |
| M30 texto vazio do Histórico igual ao de Em andamento | `BookingsScreen.tsx:22` | Morto | `bookingsScreen.test.tsx` "shows the History empty state with a way back to Home..." |
| M31 mensagem `over-limit` vazia | `messages.ts:8` | Morto | `homeSelection.test.tsx` "allows 10 occurrences and explains the limit on the 11th..." |
| M29 sem desempate por `bookingId` | `listQuery.ts:36` | Morto | `listQuery.test.ts` "breaks a createdAt tie by bookingId..." |
| M3c `confirm()` do provider sem guarda `canConfirm` | `CheckoutProvider.tsx:197` | Morto | `CheckoutProvider.test.tsx` "does not send a purchase from confirm() while the purchase capability is off" (o teste usa a capacidade desligada; a guarda de preço continua exercida por M3a/M27 no nível do botão) |

### Iteração 2: novos mutantes de READ-01

| Mutante | Resultado | Teste que matou |
| --- | --- | --- |
| M32 texto visível volta a `Saldo` | Morto | `AppHeader.test.tsx` "greets on the left and shows the confirmed balance..." |
| M33 `accessibilityLabel` volta a `Saldo:` | Morto (5 falhas) | `AppHeader`, `checkoutBadge`, `homeSelection`, `purchaseFlow`, `purchaseTracking` |

### Iteração 2: amostra de regressão (12 mutantes que já morriam, todos ainda mortos)

M1 e M1b (replay com chave ou corpo diferentes), M2 (débito local), M3a, M3b (preço sem revisão, reenvio silencioso), M4 (GET antigo), M5 e M5b (duplo toque), M6 (status desconhecido terminal), M7 (POST ao reconectar), M8 (GET com ID malformado), M9 (Retry-After), M13 (conflito de idempotência), M14 (offline), M17 (seleção não limpa), M19 (foreground), M21 (cache da carteira), M38 (total +1). Os testes que mataram são os mesmos da iteração 1, com 252 testes na suíte.

**Resultado do sensor na iteração 2:** 28 injetados, 28 mortos, 0 sobreviventes. Os 8 mínimos pedidos na iteração 1 continuam todos mortos (M1, M2, M3a/M3b, M4, M5, M6, M7, M8).

---

## Isolamento (porcelain da árvore real)

Iteração 2, `git status --porcelain` antes e depois do sensor, idêntico (diff vazio):

```
?? .omc/
?? .specs/features/cicclo-mobile/.omc/
?? .specs/features/cicclo-mobile/validation.md
```

(`validation.md` aparece porque foi criado na iteração 1; `apps/mobile/.omc/` deixou de aparecer como não rastreado porque um arquivo dele entrou no commit `db1ea75`, ver abaixo.) Worktree descartável removido (`git worktree list` mostra só o repositório principal e este worktree). Sem `git stash`, reset ou rebase. Observação de processo: durante a iteração 2 o script de sensor na pasta de scratch foi sobrescrito por outro agente; o Verifier passou a usar um script próprio em subpasta (`verifier_iter2/mut.py`) e não houve efeito sobre a árvore real.

---

## Qualidade de código e pendências menores (não bloqueiam o PASS)

| Item | Gravidade | Detalhe |
| --- | --- | --- |
| Arquivo de estado de ferramenta versionado | Baixa | `git ls-files` mostra `apps/mobile/.omc/state/sessions/5d88cbe9-3855-4ca4-a01c-dc5f13913981/pre-tool-advisory-throttle.json` dentro do commit `db1ea75`. É estado operacional que não deveria entrar no histórico. Correção: `git rm --cached` desse arquivo e ignorar `.omc/state/` (o `.omc/skills/**` pode seguir como exceção) |
| Aviso de depreciação do expo-router | Baixa | 54 `console.warn` por `unstable_settings.initialRouteName` em `src/app/(tabs)/_layout.tsx:11`; trocar por `anchor` |
| Timer de `Retry-After` em `CheckoutScreen.tsx:206-212` | Baixa | Sem teste de cleanup no desmonte |
| Teste M28 é de unidade em `classifyFailure` | Informativa | Prova a classificação, não o caminho completo do hook até o estado; o encadeamento está coberto pelos demais casos da matriz `useBookingSubmission.test.tsx:149-170` |
| Arquitetura | OK | `shared/` não importa `features/`; nenhum `fetch(` direto em `app/` ou `features/` |
| Diretrizes | OK | AD-004 (testes mínimos e escopados) e AD-005 (N1 do usuário) respeitadas |

---

## Atualização de rastreabilidade proposta (spec.md não alterado)

| Status proposto | IDs |
| --- | --- |
| Verified (parte testável sem API real) | NAV-01..03, READ-01..06, SEL-01..11, BUY-01..14, BOOK-01..07, SAFE-01..05, UX-01 (automatizável), QUAL-01, QUAL-02 (gates automáticos) |
| Pending external | UX-02, QUAL-02 (smoke Android), UX-01 (leitor de tela), gates C1/C2/C3 para leitura, compra e acompanhamento |
| Fora do escopo desta verificação | QUEUE-01..QUEUE-10 |

## Resumo

**Overall:** pronto para o escopo verificável localmente (PASS). A entrega P0 só pode ser declarada concluída depois de C1/C2/C3 e N1.

**Spec-anchored:** 48 de 50 IDs com evidência `file:linha` (0 GAP); 2 pendentes de N1 (UX-02, smoke Android de QUAL-02).
**Sensor:** 28 de 28 mortos na iteração 2 (8 sobreviventes anteriores mortos, 2 de READ-01, 18 de regressão); 0 sobreviventes.
**Gate:** typecheck 0, lint 0 avisos, 252 de 252 testes, 30 suítes.
**Próximo passo:** o usuário executa N1 e os gates C1/C2/C3 quando a API estiver pronta; remover do índice o arquivo `.omc` versionado em `db1ea75`.
