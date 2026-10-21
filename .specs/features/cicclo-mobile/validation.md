# Cicclo mobile: validação independente do escopo P0

**Data:** 2026-10-21
**Criado:** 2026-10-21. **Última atualização:** 2026-10-21 (rodada 4, estado final).
**Spec:** `.specs/features/cicclo-mobile/spec.md`
**Faixa Git:** `08e6c7d..HEAD`. Rodada 1 verificou `e16246c`; rodada 2, `db1ea75`; rodada 3, `9d4ad9f`; rodada 4 (final), `cb9adcd` (teste `CheckoutProviderPurchase.test.tsx` e ressalvas no `api-contract.md`).
**Verifier:** agente independente (autor diferente do verificador). Nenhum código de produção ou teste foi alterado na árvore real; este arquivo é a única escrita. O teste ao vivo (`apps/mobile/live/contract.live.test.ts`) **não foi executado por este Verifier** (debita a carteira demo, hoje em 3110); foi lido criticamente.

Verdict: PASS

**Result**: PASS

Condições do PASS atendidas: nenhum GAP de AC P0; nenhum mutante não equivalente sobrevivente (o N5 da rodada 3 morre agora); gates verdes (31 suítes, 254 testes). Este PASS vale para o que é verificável localmente e contra a API real pelo teste ao vivo lido. Ele **não** declara a entrega concluída: N1 (Android), P1 (QUEUE) e a compra após horas seguem à parte como PENDING-EXTERNAL. Há ressalvas de prova ao vivo registradas (seção própria) e uma imprecisão pequena na ressalva 2, sem efeito sobre o veredito.

## Escopo

**Verificado (50 IDs P0):** NAV-01..03, READ-01..06, SEL-01..11, BUY-01..14, BOOK-01..07, SAFE-01..05, UX-01..02, QUAL-01..02.

**Fora do escopo desta verificação (não é falha):** QUEUE-01..QUEUE-10 (P1, T31-T37 deliberadamente não implementadas).

## Pendente por dependência externa (à parte; não força FAIL)

| Item | Situação |
| --- | --- |
| N1 (Android real, TalkBack, fonte ampliada, safe areas) | Pendente, executado pelo usuário (AD-005). `native-validation.md` é só um roteiro. Cobre UX-02, o smoke Android de QUAL-02 e o leitor de tela de UX-01. Também é a primeira execução do transporte `fetch` real do React Native contra a API |
| QUEUE-01..10 (P1) | Não implementado, fora desta verificação |
| Compra após horas (retenção da Idempotency-Key) | Justificada por leitura do backend (chave com índice único, sem limpeza), mas o replay depois de horas nunca foi exercitado. `keyRetentionMs = null` |
| Casos ao vivo não exercitados (ver seção de reavaliação) | Compra de 2 ou mais unidades, preferência aceita, ordenação e paginação com vários bookings, estados terminais de item, status HTTP exato do replay |
| `pnpm test:bootstrap` e `pnpm lint/test/build` do workspace | Falham só por falta de `dotnet`; não executados por este Verifier |

Os gates C1, C2 e C3 deixaram de estar pendentes: ver "Reavaliação dos ACs com a API real".

## Tarefas (tasks.md)

T1-T28 com Done when `[x]` (T10, T21, T23-T26 e T28 viraram `[x]` após a prova ao vivo). T29 e T30 seguem `[~]` (N1 é do usuário). T31-T37 abertas (P1, fora de escopo).

## Comandos executados (rodada 4, árvore real, HEAD `cb9adcd`)

| Comando | Resultado real |
| --- | --- |
| `pnpm --filter @cicclo/mobile typecheck` | exit 0 |
| `pnpm --filter @cicclo/mobile lint` (`--max-warnings 0`) | exit 0, zero avisos |
| `pnpm --filter @cicclo/mobile test` | exit 0: **31 suítes / 31 passaram; 254 testes / 254 passaram; 0 falhas; 0 ignorados** (+2 testes, +1 suíte: `CheckoutProviderPurchase.test.tsx`). O teste ao vivo fica fora (`testPathIgnorePatterns` inclui `/live/`) |
| Grep `test.skip|it.skip|describe.skip|.only(|xit|xdescribe|test.todo|it.todo|TODO|FIXME|fit(|fdescribe` em `src`, `__tests__`, `live` | Nenhuma ocorrência |
| `pnpm --filter @cicclo/mobile build` | exit 0 na rodada 1; não reexecutado (desde então só rótulo, constante de capacidade, testes e docs) |

Evolução da contagem: 1 antes da feature; 242 (`e16246c`); 252 (`db1ea75`, `9d4ad9f`); 254 (`cb9adcd`). Nenhum teste removido ou enfraquecido nas rodadas 2 a 4.

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

## Isolamento (porcelain da árvore real)

Rodada 4, `git status --porcelain` antes e depois do sensor: ambos vazios, **idêntico** (`.omc/` ignorado; `validation.md` versionado). Worktree descartável removido. Sem `git stash`, reset ou rebase. O teste ao vivo não foi executado; a carteira demo não foi tocada.

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

## Planos de correção (nenhum bloqueia o PASS)

1. `api-contract.md`: na ressalva 2, trocar "o código e o formato do cabeçalho só aparecem no log" por "o código só aparece no log; o formato do `Retry-After` não foi registrado"; acrescentar 413, 415 e 503 como provenientes só da leitura do código.
2. Opcional: um caso para N4 (`canConfirm` falso durante `submitting`), para tirar a equivalência da dependência da guarda do hook.
3. Opcional: trocar `initialRouteName` por `anchor` (54 avisos) e testar o cleanup do timer de `Retry-After`.
4. Quando o usuário puder: repetir a prova ao vivo com `["wash","wash"]` e preferência aceita (requer ajustar o teste e restaurar a demo) e exercitar o replay após horas.

## Resumo

**Overall:** PASS para o escopo verificável localmente e pelo teste ao vivo lido. Entrega P0 completa só depois de N1.

**Spec-anchored:** 48 de 50 IDs com evidência `file:linha`, 0 GAP; 2 pendentes de N1 (UX-02, smoke Android de QUAL-02).
**Sensor:** rodada 4: 13 de 16 mortos; 0 sobreviventes não equivalentes (N4 e N6 equivalentes, N9 frágil por tempo, N9b morto). Os 8 mínimos da rodada 1 ficaram mortos em todas as rodadas.
**Gate:** typecheck 0, lint 0 avisos, 254 de 254 testes, 31 suítes.

**PENDING-EXTERNAL (à parte):** N1 (Android, TalkBack, fonte ampliada, primeiro uso do `fetch` do React Native contra a API); QUEUE-01..10 (P1); compra após horas (retenção da chave, `keyRetentionMs = null`); compra de 2 ou mais unidades e preferência aceita ao vivo; `pnpm test:bootstrap` e comandos do workspace (sem `dotnet`).
