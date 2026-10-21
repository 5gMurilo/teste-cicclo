# Cicclo mobile: validação independente do escopo P0

**Data:** 2026-10-21
**Criado:** 2026-10-21. **Última atualização:** 2026-10-21 (reverificação do delta, rodada 2).
**Spec:** `.specs/features/cicclo-mobile/spec.md`
**Faixa Git:** `08e6c7d..HEAD`. Herdado: rodadas 1 a 4 verificaram até `cb9adcd` (PASS da rodada 4). **Delta:** `cb9adcd..57b40ed` (log de desenvolvimento, mensagem de configuração, `anchor`, documentação iOS, registro do N1 e as correções dos dois itens da primeira verificação do delta).
**Verifier:** agente independente (autor diferente do verificador). Nenhum código de produção ou teste foi alterado na árvore real; este arquivo é a única escrita. O teste ao vivo **não foi executado** (a demo está em 3110).

Verdict: PASS

**Result**: PASS

Condições do PASS atendidas: nenhum GAP de AC P0 (escopo herdado e delta); nenhum mutante não equivalente sobrevivente (L1, L2 e L2b, os do delta, morrem agora); gates verdes (32 suítes, 262 testes); o registro do N1 não afirma mais do que "Validado o iOS". Este PASS não declara a entrega concluída: Android, P1, a compra após horas e a compra de 2 ou mais unidades ao vivo seguem PENDING-EXTERNAL, e há recomendações abertas de baixa gravidade (T30, contagens, texto), listadas abaixo.

## Delta `cb9adcd..57b40ed`

### Comandos executados (árvore real, HEAD `57b40ed`)

| Comando | Resultado real |
| --- | --- |
| `pnpm --filter @cicclo/mobile typecheck` | exit 0 |
| `pnpm --filter @cicclo/mobile lint` (`--max-warnings 0`) | exit 0, zero avisos |
| `pnpm --filter @cicclo/mobile test` | exit 0: **32 suítes / 32 passaram; 262 testes / 262 passaram; 0 falhas; 0 ignorados** (+8 desde `cb9adcd`: 5 de log em `client.test.ts`, 2 de `configError`, 1 de `navigation`) |
| Grep `test.skip|it.skip|describe.skip|.only(|xit|xdescribe|test.todo|it.todo|TODO|FIXME|fit(|fdescribe` | Nenhuma ocorrência |
| Teste ao vivo e `pnpm build` | Não executados (carteira demo; sem mudança de empacotamento) |

### Revisão do código novo (resumo; achados da primeira verificação do delta)

| Pergunta | Achado |
| --- | --- |
| O log vaza dado sensível? | Não: só `kind`, `status`, `errorCode`, `traceId` e, para `ConfigError`, textos fixos de `env.ts`. Sem corpo, cabeçalhos, `Idempotency-Key` nem query (`client.test.ts:185-204`). Informativo: o caminho ainda traz o ID do booking e o `traceId` vai ao terminal em desenvolvimento |
| Cancelamento, modo teste e produção ficam mudos? | **Agora sim, com prova.** Cancelamento: `client.test.ts:224` (L5 morto). Release (`__DEV__` falso): teste "stays silent in a release bundle, where __DEV__ is false", que força `globalThis.__DEV__ = false` e restaura no `finally` (L1 morto). Modo teste: teste "stays silent under Jest (NODE_ENV test) even in a development bundle" (L2 morto). Sem as duas guardas, 2 falhas (L2b morto) |
| A mensagem de configuração aparece fora de configuração? | Não (`instanceof ConfigError`); L7, L13, L14, L15 e L16 morreram na primeira verificação do delta |
| O retry some só em `ConfigError`? | Sim; L8, L9 e L10 morreram (L8 reconfirmado nesta rodada) |
| Algum AC P0 quebrou? | Não: READ-02, READ-03, BOOK-06, SAFE-02 e UX-01 intactos. Precisão: com URL ausente o header mostra a instrução de configuração, sem retry (READ-03 não trata falha de configuração) |
| `anchor` | Teste prova ausência do aviso e Home em `/`; L11 morto. L12 (remover `anchor`) segue equivalente para os ACs P0 |

### Registro do N1: releitura linha a linha

O usuário disse apenas "Validado o iOS". Arquivos relidos integralmente nas partes alteradas e nas vizinhas: `native-validation.md`, `README.md`, `tasks.md` (T29, T30, cabeçalho e "Estado inicial"), `STATE.md` (AD-006 e Handoff).

| Local | Afirmação | Avaliação |
| --- | --- | --- |
| `native-validation.md`, "Estado" | "Aprovado pelo usuário no iOS em relato global... sem registro por cenário, aparelho, versão do iOS, leitor de tela ou texto ampliado" | Fiel |
| `native-validation.md`, "Identificação" | Commit, URL e saldo: "não informado pelo usuário"; o commit `b80fe36` e as instruções com `127.0.0.1:5001` aparecem como registro do agente. Aparelho e versão: "não informado (havia um simulador iPhone 17 ligado)" | Fiel. O parêntese sobre o simulador ligado é observação do agente e não afirma que o ensaio o usou |
| `native-validation.md`, "Resultados" | Cabeçalho: "o único registro é o relato global... nenhum cenário abaixo está marcado como aprovado individualmente"; os 8 cenários "SEM REGISTRO POR CENÁRIO" | Fiel (corrige o achado da primeira verificação do delta). Detalhe de redação: "relato global abaixo" deveria ser "acima" |
| `native-validation.md`, "Limites" | Nenhum cenário individual afirmado; Android, TalkBack e fonte 200% não ensaiados | Fiel |
| `native-validation.md`, "Plataforma deste ensaio" | "decisão do usuário... por ser mais simples de executar" | A decisão está em AD-006; o motivo é o registrado pelo coordenador e não foi verificado por este Verifier. Informativo |
| `README.md`, estado e mapa de capacidades | "Aprovada pelo usuário no iOS, em relato global ("Validado o iOS"), sem registro por cenário; Android não ensaiado"; sem "aparelho", "VoiceOver" nem "texto ampliado" | Fiel |
| `STATE.md` AD-006 e Handoff | "apenas em relato global, sem registro por cenário, aparelho, versão ou leitor de tela; por isso T29 fica `[~]`" | Fiel. Observações: "P0 implementado e verificado (T1 a T30)" antecipa a verificação, e a contagem "260 testes" está defasada (262) |
| `tasks.md` T29 | `[~]` | Correto |
| `tasks.md` T30 | `[x]` | **Não se sustenta com T29 `[~]`:** T30 "Depends on: T29", o Gate inclui "N1", e o Done when exige "todos P0 têm prova", o que é falso para UX-02 e o smoke Android de QUAL-02 (sem prova) e para o iOS (sem registro por cenário). Recomendação: T30 `[~]`, ou reescrever o Done when para "documentação publicada" |
| `tasks.md`, "Estado inicial e retomada" (linha 714) | "C1/C2/C3/CP1 não comprovadas; N1 não executado" | Defasada desde a prova ao vivo e o relato do iOS; contradiz o restante. Baixa |

Nenhuma afirmação por cenário, aparelho, versão, VoiceOver ou texto ampliado restou nos quatro arquivos. UX-02 e o smoke Android de QUAL-02 seguem PENDING-EXTERNAL; a decisão do iOS no lugar do Android (AD-006) é do usuário.

### Sensor do delta, rodada 2 (worktree descartável em `57b40ed`)

| Mutante | Descrição | Resultado | Teste que matou |
| --- | --- | --- | --- |
| **L1** | log sem a guarda `__DEV__` | **Morto** | `client.test.ts` "stays silent in a release bundle, where __DEV__ is false" |
| **L2** | log sem a guarda de modo teste | **Morto** | `client.test.ts` "stays silent under Jest (NODE_ENV test) even in a development bundle" |
| **L2b** | log sem nenhuma guarda | **Morto** (2 falhas) | os dois testes acima |
| L3 | log inclui o corpo | Morto | "logs only the classification and the path..." |
| L5 | log de requisição cancelada | Morto | "does not log a request cancelled by the caller" |
| L6 | log mantém a query string | Morto | idem L3 |
| L8 | `isConfigError` sempre falso | Morto | `configError.test.tsx` (2) |
| L11 | `anchor` revertido para `initialRouteName` | Morto | `navigation.test.tsx` "anchors the tabs on Home..." |
| M1 | replay com chave nova (regressão de compra) | Morto | `useBookingRecovery.test.tsx` e `purchaseFlow.test.tsx` |
| N8 | botão de compra sem `disabled` (regressão) | Morto | `lifecycle.test.tsx` e `purchaseFlow.test.tsx` |

**Resultado:** 10 injetados, 10 mortos, 0 sobreviventes. L12 (remover `anchor`) permanece equivalente para os ACs P0 e não foi reinjetado. **Sobreviventes não equivalentes: 0.**

### Isolamento

`git status --porcelain` da árvore real antes e depois: vazio nos dois, **idêntico**. Worktree descartável removido; sem `git stash`, reset ou rebase; teste ao vivo não executado.

## Pendente por dependência externa (à parte; não força FAIL)

| Item | Situação |
| --- | --- |
| N1 no Android (TalkBack, fonte 200%, safe areas) | Não ensaiado. O iOS foi aprovado só em relato global, sem registro por cenário (AD-006). UX-02 e o smoke Android de QUAL-02 seguem sem prova |
| QUEUE-01..10 (P1) | Não implementado, fora desta verificação |
| Compra após horas (retenção da Idempotency-Key) | Inferida por leitura do backend; replay depois de horas nunca exercitado; `keyRetentionMs = null` |
| Compra ao vivo de 2 ou mais unidades e preferência aceita | Não exercitada (a demo está em 3110) |
| `pnpm test:bootstrap` e `pnpm lint/test/build` do workspace | Falham só por falta de `dotnet`; não executados por este Verifier |

## Escopo herdado (verificado até `cb9adcd`, sem alteração nesta rodada)

As seções seguintes são a verificação das rodadas 1 a 4. Citações `file:linha` valem para `cb9adcd`; no delta mudaram `navigation.test.tsx` (um teste acrescentado no primeiro bloco, que desloca as citações posteriores) e `client.test.ts`, `AppHeader.tsx`, `balanceState.ts`, `HomeScreen.tsx`, `BookingsScreen.tsx` e `BookingDetailsScreen.tsx` (ramos de configuração e log, sem tocar os ramos citados).

## Reavaliação dos ACs com a API real (leitura crítica do teste ao vivo)

O teste ao vivo usa o transporte (`requestJson`), os schemas Zod, `createBooking` e `classifyFailure` do próprio app, sobre um `fetch` de Node (`http`), contra a API em `127.0.0.1:5001`. **Não prova** o fluxo de telas, o hook `useBookingSubmission` nem o `fetch` do React Native. A execução foi relatada pelo coordenador e registrada em `docs/api-contract.md`; este Verifier não a reproduziu.

| ID | Antes | Agora | Evidência com fake | Evidência ao vivo (`live/contract.live.test.ts`) e ressalvas |
| --- | --- | --- | --- | --- |
| BUY-01 | COVERED, contrato lido do código | COVERED | `purchaseFlow.test.tsx:120-121,245,256`; `createBooking.test.ts:73-76` | `:127-130` compra de 1 lavagem aceita com `Idempotency-Key` UUID e corpo do app (1890, 5000 para 3110); `:91` sem chave 400; `:92` 11 códigos 400; `:121-122` preferência fora do lote 400, o que prova que o nome do campo `preferredFirstServiceCode` é reconhecido. **Ressalva não resolvida:** duplicatas (`wash,wash`) só foram provadas por recusa (`:95`, total errado dá `PRICE_CHANGED`); nenhuma compra com 2 ou mais unidades nem preferência aceita foi exercitada ao vivo. O doc registra isso, mas a linha "duplicatas são precificadas como itens distintos" é inferência |
| BUY-04 | COVERED (fake) | COVERED | `useBookingSubmission.test.tsx:123-134`; `purchaseFlow.test.tsx:133-134` | `:136-139` replay devolve o mesmo `bookingId` e `chargedAmountCents` 1890. **Ressalva não registrada:** `createBooking` esconde o status HTTP, então "201 na criação e 200 no replay" continua vindo da leitura do código, e `replay.remainingBalanceCents` não é asserido (só o `GET /wallet` seguinte, `:139`) |
| BUY-05 | COVERED (fake), consistência pendente C2 | COVERED | `applyConfirmedBooking.test.tsx:53`; `purchaseFlow.test.tsx:123` | `:134` o `GET /wallet` imediatamente depois do POST já retorna 3110. **Ressalva não registrada:** é uma única amostra; não prova leitura-após-escrita em geral nem a corrida de um GET em voo. A proteção do app (cancelar GET em voo antes de gravar) não depende dessa propriedade |
| BUY-08 | COVERED (fake) | COVERED | `useBookingSubmission.test.tsx:149-170,201` | Só o conflito de chave (`:141-143`, `unknown/idempotency-conflict`). Timeout, rede, 5xx e 2xx inválido nunca foram produzidos pela API real; ficam cobertos por fake. O 503 do doc vem da leitura do código |
| BUY-09 | COVERED (fake) | COVERED | `useBookingRecovery.test.tsx:99-101`; `purchaseFlow.test.tsx:206-207` | `:126-139` replay com a mesma chave e o mesmo corpo serializado (`sent`) devolve o mesmo booking sem segundo débito (`wallet` 3110 em `:139`) |
| BUY-12 | COVERED (fake), `Retry-After` pendente C2 | COVERED | `useBookingRecovery.test.tsx:191-200,208,215` | `:106-110` a sexta chamada devolve 429, `retryAfterAt` não nulo e `classifyFailure` dá `rejected/rate-limited`. **Ressalva não registrada:** o `errorCode` `RATE_LIMIT_EXCEEDED` citado no doc só é `console.log`, não asserido, e o formato do `Retry-After` (segundos ou data) não foi registrado |
| BUY-13 | COVERED | COVERED | `useBookingSubmission.test.tsx:182-183`; `purchaseFlow.test.tsx:227-229` | `:141-144` mesma chave com outro corpo dá 409 `IDEMPOTENCY_CONFLICT`, classificado `unknown` e carteira intacta |
| SAFE-01 | COVERED (fake) | COVERED | testes de schema da iteração 1 | Todas as respostas reais passam pelos schemas do app: carteira, catálogo, lista, detalhe e `createBooking` (enum estrito de status) |
| SAFE-02 | COVERED (fake) | COVERED | `apiError.test.ts`; `client.test.ts` | `:97-101,118,122,142,170,172` ProblemDetails reais de 400, 404, 409 e 429 classificados por `errorCode`. Corpo HTML ou vazio não foi produzido pela API real (só fake) |
| READ-01 | COVERED (fake) | COVERED | `AppHeader.test.tsx:16-18`; `purchaseFlow.test.tsx:123` | `:72` carteira real 5000 BRL passa o schema. A tela com dado real só no N1 |
| READ-04 | COVERED (fake) | COVERED | `home.test.tsx:62-65`; `catalog/__tests__/queries.test.ts` | `:73-77` catálogo real `dry` 2090 e `wash` 1890 contratáveis, pelo schema do app (nomes não asseridos ao vivo) |
| BOOK-01 | COVERED (fake) | COVERED | `bookingsScreen.test.tsx:80-83,180`; `listQuery.test.ts:73-94` | `:156-158` a lista contém exatamente o booking novo, `page` 1, `pageSize` 20, `hasNextPage` false. **Ressalva não registrada:** o título diz "newest first", mas só existe um booking; a ordenação e a paginação com `hasNextPage` verdadeiro nunca foram vistas ao vivo (`:176-177` só testa página vazia). O app reordena no cliente, o que limita o risco |
| BOOK-05 | COVERED (fake) | COVERED | `bookingDetails.test.tsx:79-86`; `detailQuery.test.ts:51-56` | `:160-163` detalhe com preço 1890. **Ressalva:** igual ao catálogo atual, então o teste ao vivo não distingue preço histórico de preço atual; essa distinção só existe no fake |
| BOOK-06 | COVERED (fake) | COVERED | `bookingDetails.test.tsx:121-135`; `detailQuery.test.ts:60-69` | `:169-172` 404 `BOOKING_NOT_FOUND` para UUID inexistente e 400 `VALIDATION_ERROR` para `abc` (o app nem envia) |

**Afirmações do `api-contract.md` sem prova ao vivo ou com ressalva não registrada:** (1) 201 versus 200 do replay; (2) `RATE_LIMIT_EXCEEDED` e formato de `Retry-After`; (3) leitura consistente generalizada a partir de uma amostra; (4) "ordem `createdAt` e `bookingId` decrescentes" e paginação com várias páginas; (5) estados terminais de item (só `to_be_confirmed` foi visto, então BOOK-02/BOOK-03 e a lista de status do enum seguem apoiados no fake e no código do backend); (6) 413, 415 e 503; (7) retenção da chave (registrada). Nenhuma contradiz o app; são lacunas de prova do doc, não defeitos do app. Na rodada 4 essas ressalvas passaram a constar no `api-contract.md` (conferidas na seção seguinte).

## Conferência das 7 ressalvas do `api-contract.md` contra o teste ao vivo

Seção "Ressalvas da prova ao vivo" (`apps/mobile/docs/api-contract.md`, linhas 30-38), comparada com `apps/mobile/live/contract.live.test.ts`:

| # | O que a ressalva diz | O que o teste faz de fato | Fidelidade |
| --- | --- | --- | --- |
| 1 | 201 e 200 não observados; `createBooking` devolve só o corpo; prova mesmo `bookingId`, mesmo valor cobrado, sem novo débito; `remainingBalanceCents` do replay não afirmado | `:136-139` asserta `replay.bookingId`, `chargedAmountCents` e `wallet()` igual a 3110; `createBooking` (`createBooking.ts`) retorna o corpo validado | Fiel |
| 2 | 429 observado (`RATE_LIMIT_EXCEEDED`, prazo de `Retry-After`); código e formato do cabeçalho só no log, sem asserção | `:106-110` asserta `status 429`, `retryAfterAt` não nulo e a classificação; `:107` só faz `console.log` de `status`, `errorCode` e `retryAfterAt` | **Quase fiel, uma imprecisão:** o `errorCode` está no log sem asserção (correto), mas o **formato** do `Retry-After` (segundos ou data) não aparece em lugar nenhum, porque o log mostra o prazo já convertido (`retryAfterAt`), não o cabeçalho bruto. Deveria dizer "o formato não foi registrado" |
| 3 | Leitura consistente de uma única amostra | `:134` um único `wallet()` após o POST | Fiel |
| 4 | Ordenação e paginação com `hasNextPage` verdadeiro não vistas ao vivo; provadas com API simulada | Um só booking (`:157`); `:176-177` só página vazia; ordenação e página 2 cheia em `listQuery.test.ts:73-94,97-115` e `bookingsScreen.test.tsx` | Fiel |
| 5 | Só `to_be_confirmed` visto ao vivo; terminais e `queued` provados com simulada e pela leitura do backend | `:164-165` apenas registra os status vistos; nenhum teste asserta o status | Fiel (o status visto só aparece em log, o que reforça a ressalva) |
| 6 | Duplicatas inferidas de uma recusa; nenhuma compra de 2 ou mais unidades nem preferência aceita com débito | `:95` e `:101` duplicatas por recusa; `:121-122` preferência fora do lote por recusa; só `:127` compra com uma lavagem | Fiel. O passo "restaurar a demo e repetir com `["wash","wash"]` e preferência" exige alterar o teste, que hoje compra uma lavagem fixa |
| 7 | `fetch` do Node e não o do React Native; preço histórico 1890 igual ao atual | `:26-49` `fetch` próprio sobre `http`; `:163` 1890 | Fiel |

Ainda sem ressalva registrada, de menor peso: 413, 415 e 503 vêm só da leitura do código do backend; a linha da tabela principal que afirma "201 na criação e 200 no replay" e "sexta chamada retorna 429 `RATE_LIMIT_EXCEEDED`" continua escrita como observação, embora as ressalvas 1 e 2 a qualifiquem.

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
| SEL-02 | Excede saldo: mantém seleção + mensagem | `CheckoutProvider.test.tsx:108-110` `{ok:false, reason:'insufficient-balance'}`, `quantities {wash:1}`; `homeSelection.test.tsx:94-96` `'Saldo insuficiente para adicionar este serviço.'`. M23 morto | COVERED |
| SEL-03 | Bloquear adições e contratação sem dados confiáveis ou sem confirmação após reconexão | Adições: `CheckoutProvider.test.tsx:122,134`; `homeSelection.test.tsx:99-109`; `lifecycle.test.tsx:96-158`. **Contratação (novo):** `lifecycle.test.tsx:164` `toBeEnabled()` com dados confiáveis, `:167` `toBeDisabled()` offline, `:174` `toBeDisabled()` durante a releitura pendente, `:177` volta a `toBeEnabled()`, `:178` `posts` igual a 0. M26 e M14 mortos | COVERED (era GAP) |
| SEL-04 | 10 permitido; 11ª impedida com explicação | `CheckoutProvider.test.tsx:160-162` `over-limit`; `checkoutSelectors.test.ts:61-64,136`; **UI (novo):** `homeSelection.test.tsx:122` `getByRole('alert')` `toHaveTextContent('Limite de 10 serviços por contratação.')`, `:123` seleção mantida em `R$ 10,00`; `:119` sem alerta até a 10ª. M31 e M11 mortos | COVERED (era GAP) |
| SEL-05 | Quantidade 2 vira duas entradas | `checkoutSelectors.test.ts:43`; `homeSelection.test.tsx:75`; `createBooking.test.ts:49`. M24 morto | COVERED |
| SEL-06 | Seleção preservada entre abas | `CheckoutProvider.test.tsx:89` `quantity 1`; `checkoutBadge.test.tsx:62-67` | COVERED |
| SEL-07 | Última ocorrência limpa a preferência | `checkoutReducer.test.ts:48-50`; `CheckoutProvider.test.tsx:198`; `checkoutReview.test.tsx:100-102`. M25 morto | COVERED |
| SEL-08 | Mudança de preço/elegibilidade bloqueia consentimento antigo até nova revisão | Preço: `checkoutReview.test.tsx:111-119`; `purchaseFlow.test.tsx:177-182` botão `toBeDisabled()` até `Aceitar novos preços`. **Elegibilidade e saldo (novo):** `purchaseFlow.test.tsx:267` botão `toBeDisabled()` com serviço desativado após a seleção, `:278` e `:280` `toBeDisabled()` quando o novo preço (6000) excede o saldo, antes e depois de `Aceitar novos preços`, `posts` 0. M3a e M27 mortos | COVERED (era GAP) |
| SEL-09 | Vazio: `Escolher serviços`, sem POST | `checkoutReview.test.tsx:70-74` | COVERED |
| SEL-10 | Três rótulos distintos | `checkoutReview.test.tsx:88-90`. M2b morto | COVERED |
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
| BUY-14 | Aviso de cobrança imediata e simulação | `checkoutReview.test.tsx:137-138` | COVERED |

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
| UX-01 | Role, label e estado coerentes; feedback em texto | `ServiceCard.test.tsx:52,67`; `checkoutReview.test.tsx:95-102`; `bookingsScreen.test.tsx:103`; `purchaseFlow.test.tsx:178`; `homeSelection.test.tsx:94`; `ServiceCard.test.tsx:33-38` | COVERED na parte automatizável; leitor de tela PENDING-EXTERNAL |
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


### Iteração 3: caminho novo criado por `purchase: true` (worktree descartável em `9d4ad9f`)

| Mutante | Descrição | Resultado | Teste que matou ou análise |
| --- | --- | --- | --- |
| N1 | `canConfirm` sem `capabilities.purchase &&` | Morto | `CheckoutProvider.test.tsx` "does not send a purchase from confirm() while the purchase capability is off" |
| N2 | `purchase.enabled` sempre `true` (botão visível sem capacidade) | Morto (2) | `checkoutReview.test.tsx` "...exposes no purchase action"; `CheckoutProvider.test.tsx` mesmo caso |
| N3 | Tela sempre renderiza o botão | Morto | `checkoutReview.test.tsx` "...exposes no purchase action" |
| N7 | readiness enfraquecida (`!== 'loading'`: dado indisponível libera) | Morto | `lifecycle.test.tsx` "disables the purchase button offline and while the re-read is pending, with no POST" |
| N8 | botão nunca desabilitado (`disabled={false}`) | Morto (4) | `lifecycle.test.tsx`; `purchaseFlow.test.tsx` "disables the purchase button when a selected service becomes unavailable" |
| N10 | sem revisão de preços (preços vistos não registrados ao adicionar) | Morto (3) | `checkoutReview.test.tsx` "requires a new review when a price changes..."; `purchaseFlow.test.tsx` "keeps the purchase button disabled when a price rise..." |
| N9b | janela de retenção da chave fixada em 0 (provider repassa o valor ao hook) | Morto (2) | `lifecycle.test.tsx` "never re-sends an unknown purchase..."; `purchaseFlow.test.tsx` "keeps an unknown outcome recoverable..." |
| **N5** | `canConfirm` aceita seleção vazia (`derived.quantity > 0` removido, `CheckoutProvider.tsx:170`) | **SOBREVIVEU** | Nenhum teste afirma `canConfirm` falso com seleção vazia e compra habilitada. A UI não chega ao botão (a tela mostra o estado vazio antes), mas `purchase.confirm()` programático enviaria `{serviceCodes: [], expectedTotalCents: 0}` à API real. Correção: teste com `purchase: true` (por exemplo em `purchaseFlow.test.tsx`, ou hook com provider) chamando `confirm()` sem seleção e asserindo `canConfirm === false` e `posts` 0 |
| N4 | `canConfirm` ignora o estado da submissão (`status === 'idle'` removido) | Sobreviveu, classificado equivalente | Durante `submitting` ou `unknown` a tela mostra outra visão e a guarda do hook (`useBookingSubmission.ts`, `submit`) bloqueia; após `confirmed` a seleção está limpa (quantidade 0). Só muda o valor do booleano exposto no contexto. Mantido como observação baixa |
| N6 | `canConfirm` e `confirm()` aceitam total `null` | Sobreviveu, equivalente | Total `null` implica `data-unavailable`, `invalid-quantity`, `unknown-service` ou `overflow`, que já bloqueiam por `problems` ou `readiness`; a guarda é redundante |
| N9 | janela de retenção fixada em 1 ms | Sobreviveu, frágil por tempo | A verificação ocorre em menos de 1 ms no teste; o mesmo mutante com 0 (N9b) morre, o que prova que o repasse provider para hook é testado |

### Iteração 3: amostra de regressão (7 mutantes antigos, todos ainda mortos)

M1 (chave nova no replay), M4 (GET antigo sobrescreve saldo), M5b (guarda de duplo toque lê estado React), M7 (POST ao reconectar), M20 (preferência não enviada), M28 (timeout vira rejeição), M9 (`Retry-After` ignorado). Testes que mataram: os mesmos das iterações anteriores.

**Resultado do sensor na iteração 3:** 18 injetados (10 novos e N9b mais 7 antigos), 14 mortos, 4 sobreviventes (N5 não equivalente; N4, N6 equivalentes; N9 frágil por tempo, resolvido por N9b). Contagem estrita de sobreviventes não equivalentes: 1 (N5).

### Rodada 4: reinjeção em worktree descartável novo em `cb9adcd`

| Mutante | Descrição | Resultado | Análise ou teste que matou |
| --- | --- | --- | --- |
| **N5** | `canConfirm` aceita seleção vazia (`derived.quantity > 0` removido, `CheckoutProvider.tsx:170`) | **Morto** | `CheckoutProviderPurchase.test.tsx` "is enabled with data ready but refuses an empty selection: no request, still idle" (`:56-61`: `enabled` verdadeiro, `canConfirm` falso, `confirm()` mantém `idle` e `posts` com 0 requisições) |
| N4 | `canConfirm` ignora o estado da submissão | Sobreviveu, **classificação mantida: equivalente** | O teste novo não exercita estados não ociosos. Durante `submitting` e `unknown` a tela troca de visão e a guarda do hook bloqueia; após `confirmed` a seleção está vazia (e o N5 agora é barrado). Só muda o booleano exposto no contexto |
| N6 | `canConfirm` e `confirm()` aceitam total `null` | Sobreviveu, **classificação mantida: equivalente** | Total `null` implica `problems` ou `readiness` já bloqueando; guarda redundante. O teste novo não muda isso |
| N9 | Janela de retenção fixada em 1 ms | Sobreviveu, **classificação mantida: frágil por tempo** | Verificação em menos de 1 ms no teste. N9b (0 ms) morre em `lifecycle.test.tsx` e `purchaseFlow.test.tsx`, o que prova que o repasse provider para hook é testado |
| N9b | Janela fixada em 0 | Morto | idem |

Os testes novos (`CheckoutProviderPurchase.test.tsx`) só alteraram o estado do N5; N4, N6 e N9 permanecem como estavam.

### Rodada 4: amostra no caminho de compra (todos mortos)

| Mutante | Descrição | Teste que matou |
| --- | --- | --- |
| N1 | `canConfirm` sem `capabilities.purchase` | `CheckoutProvider.test.tsx` "does not send a purchase from confirm() while the purchase capability is off" |
| N7 | readiness enfraquecida (`!== 'loading'`) | `lifecycle.test.tsx` "disables the purchase button offline and while the re-read is pending, with no POST" |
| M26 | `canConfirm` sem `readiness === 'ready'` | idem |
| M3a | `canConfirm` sem `priceChanges` | `purchaseFlow.test.tsx` "after PRICE_CHANGED requires accepting the new price..." |
| N10 | sem revisão de preços (preços vistos não registrados) | `checkoutReview.test.tsx`; `purchaseFlow.test.tsx` |
| N8 | botão sem `disabled` | `lifecycle.test.tsx`; `purchaseFlow.test.tsx` (4 falhas) |
| M27 | `canConfirm` sem `derived.problems` | `purchaseFlow.test.tsx` (2 falhas) |
| M1 | replay com chave nova | `useBookingRecovery.test.tsx`; `purchaseFlow.test.tsx` |
| M4 | sem `cancelQueries` (GET antigo sobrescreve o saldo) | `applyConfirmedBooking.test.tsx` "keeps the confirmed balance when a GET that started before the purchase answers late" |
| M5b | guarda de duplo toque lê estado React | `useBookingSubmission.test.tsx` "sends at most one initial request..." |
| M20 | preferência não enviada | `purchaseFlow.test.tsx` "sends the chosen execution preference in the request body" |

**Resultado do sensor na rodada 4:** 16 injetados (N5, N4, N6, N9, N9b mais 11 de amostra): 13 mortos, 3 sobreviventes (N4, N6 equivalentes; N9 frágil por tempo e resolvido por N9b). **Sobreviventes não equivalentes: 0.**

## Qualidade de código e pendências menores

| Item | Gravidade | Detalhe |
| --- | --- | --- |
| Estado de ferramenta versionado | Resolvido | `.omc/` ignorado e arquivo removido do índice (`afb9702`) |
| Aviso de depreciação do expo-router | Baixa | 54 `console.warn` por `unstable_settings.initialRouteName` em `src/app/(tabs)/_layout.tsx:11` |
| Timer de `Retry-After` em `CheckoutScreen.tsx:206-212` | Baixa | Sem teste de cleanup no desmonte |
| Doc `api-contract.md` afirma além do observado | Baixa, quase resolvida | As 7 ressalvas foram registradas (conferidas acima); falta corrigir a ressalva 2 (formato do `Retry-After` não foi registrado) e citar 413, 415 e 503 como vindos só do código |
| Teste ao vivo depende de ordem e de base limpa | Informativa | `process.env.LIVE_BOOKING_ID` passa entre testes; a janela de 5 chamadas por minuto do limite de taxa é contada pelo desenho do teste. Documentado no cabeçalho do arquivo |
| Teste ao vivo usa `fetch` de Node | Informativa | Não exercita o `fetch` do React Native; coberto só pelo N1 |
| Arquitetura | OK | `shared/` não importa `features/`; sem `fetch(` direto nas telas |

## Atualização de rastreabilidade proposta (spec.md não alterado)

| Status proposto | IDs |
| --- | --- |
| Verified (parte testável localmente e contra a API real) | NAV-01..03, READ-01..06, SEL-01..11, BUY-01..14, BOOK-01..07, SAFE-01..05, UX-01 (automatizável), QUAL-01, QUAL-02 (gates automáticos) |
| Pending external | UX-02, QUAL-02 (smoke Android), UX-01 (leitor de tela), compra após horas |
| Fora do escopo desta verificação | QUEUE-01..QUEUE-10 |

## Recomendações abertas (não bloqueiam o PASS)

1. **T30 `[x]` não se sustenta com T29 `[~]`** (depende de T29, Gate N1, "todos P0 têm prova"): marcar `[~]` ou reescrever o Done when. Baixa-média, bookkeeping.
2. Atualizar as contagens defasadas: `README.md:98` e `STATE.md` (Handoff) dizem 260 testes; são 262. Corrigir `tasks.md:714` ("C1/C2/C3/CP1 não comprovadas; N1 não executado"). Baixa.
3. Trocar "relato global abaixo" por "acima" na tabela de Resultados; remover "verificado" de "P0 implementado e verificado" no Handoff enquanto T29 e T30 estiverem em aberto. Informativa.
4. Herdadas, opcionais: caso para N4; linha principal do `api-contract.md` que ainda escreve "201/200" e "429 `RATE_LIMIT_EXCEEDED`" como observados.

## Resumo

**Overall:** PASS para o escopo verificável localmente e pelo teste ao vivo lido; entrega P0 completa só com os itens PENDING-EXTERNAL resolvidos (Android) ou aceitos pelo usuário.

**Escopo herdado de `cb9adcd`:** 48 de 50 ACs com evidência `file:linha`, 0 GAP; UX-02 e o smoke Android de QUAL-02 pendentes.
**Delta:** nenhum AC P0 quebrado; gates verdes (32 suítes, 262 testes); registro do N1 fiel ao relato.
**Sensor do delta:** 10 de 10 mortos nesta rodada (L1, L2 e L2b incluídos); 0 sobreviventes não equivalentes.
**Gaps por gravidade:** baixa-média, T30 `[x]`; baixa, contagens e `tasks.md:714` defasados; informativa, redação e ID de booking/`traceId` no log de desenvolvimento.
