# Cicclo mobile: validação independente (P0 mais P1)

**Data:** 2026-10-21
**Criado:** 2026-10-21. **Última atualização:** 2026-10-21 (reverificação final de P1, rodada 3).
**Spec:** `.specs/features/cicclo-mobile/spec.md`
**Faixa Git:** `08e6c7d..HEAD`. P0: verificado até `57b40ed` (PASS), preservado abaixo. P1: rodada 1 em `3643c77` (FAIL), rodada 2 em `1965e45` (FAIL por Q23), **rodada 3 em `fcd69ea`** (branch `feat/mobile-06-service-lifecycle`, um teste novo). As rodadas anteriores estão preservadas.
**Verifier:** agente independente (autor diferente do verificador). Nenhum código de produção ou teste foi alterado na árvore real; este arquivo é a única escrita. O teste ao vivo **não foi executado** e **nenhum POST foi enviado** à API.

Verdict: PASS

**Result**: PASS

Condições do PASS atendidas: os 10 ACs de P1 têm evidência e nenhum GAP testável sem a API real; nenhum mutante não equivalente vivo (Q23 morre agora; a amostra de regressão de 11 mutantes continua morta); gates verdes (36 suítes, 335 testes); P0 sem regressão. O PASS cobre o que se verifica localmente e pelas provas ao vivo já registradas. **Não declara P1 entregue por completo:** os itens PENDING-EXTERNAL abaixo (item `queued` real, corpo real do cancel, repetições, compra após horas, compra de 2 ou mais unidades, Android) seguem sem prova real ou sem ensaio, e três linhas "observado" do `api-contract.md` dependem de logs perdidos.

# Parte P1, rodada 3 (final; HEAD `fcd69ea`)

## Comandos executados (árvore real)

| Comando | Resultado real |
| --- | --- |
| `pnpm --filter @cicclo/mobile typecheck` | exit 0 |
| `pnpm --filter @cicclo/mobile lint` (`--max-warnings 0`) | exit 0, zero avisos |
| `pnpm --filter @cicclo/mobile test` | exit 0: **36 suítes / 36 passaram; 335 testes / 335 passaram; 0 falhas; 0 ignorados** (+1 teste desde `1965e45`) |
| Grep `test.skip|it.skip|describe.skip|.only(|xit|xdescribe|test.todo|it.todo|TODO|FIXME|fit(|fdescribe` | Nenhuma ocorrência |
| Diff `1965e45..fcd69ea` | Só `__tests__/itemCommands.test.tsx` (+16 linhas); nenhum arquivo de produção mudou |
| Teste ao vivo; POST à API | Não executados |

## Fechamento de Q23 (QUEUE-07, robustez do estorno)

| Item | Evidência `file:linha` + asserção | Resultado |
| --- | --- | --- |
| Um problema de cache não transforma um cancelamento confirmado em falha | `__tests__/itemCommands.test.tsx:495` `jest.spyOn(queryClient, 'cancelQueries').mockRejectedValue(new Error('cache exploded'))` com resposta 200 válida; `:500` `findByText(/Estorno confirmado/)` presente; `:501` `queryByText(/Não foi possível concluir a ação/)` nulo; `:502` `posts` com 1 envio | **COVERED** |

QUEUE-01, 02, 04 e 07 seguem COVERED com as evidências da rodada 2 (as citações de `IC` posteriores à linha 490 passaram a `IC:522-523`). Com isso, **P1: 10 de 10 ACs COVERED, 0 GAP**.

## Sensor da rodada 3 (worktree descartável em `fcd69ea`)

12 mutantes injetados, `typecheck` exit 0 em todos, **12 mortos, 0 sobreviventes**.

| Mutante | Descrição | Teste que matou |
| --- | --- | --- |
| **Q23** | `cancelItem.ts` sem o `try/catch` em volta da escrita do saldo | `itemCommands.test.tsx` "keeps a confirmed cancellation and its refund even when the wallet cache cannot be updated" (1 falha) |
| Q5 | saldo creditado numa falha | `IC` "leaves the cached balance untouched..." / "also leaves the balance untouched when a success response is not valid" |
| Q16 | grava o valor estornado como saldo | `IC` "writes the remaining balance the API returned..." |
| Q18 | detalhe não relido no zero | `IC` "re-reads the detail when the offer deadline reaches zero..." |
| Q14b | capacidade `confirm` ignorada | `bookingDetails.test.tsx` "hides Confirmar for a live offer..." |
| Q1 | confirmar no zero | `IC` "does not send when the offer ran out while the app was away..." |
| Q4 | texto de estorno sem checar o valor | `IC` "shows no refund text when the API credited nothing" |
| Q8 | polling sem a guarda de terminal | `polling.test.tsx` (detalhe 5 s; lista tudo terminal) |
| Q25 | `cache.ts` volta a aguardar a releitura | `IC` "writes the remaining balance... does not wait for the wallet read" |
| Q13 | comando sem a guarda de duplo toque | `src/features/bookings/__tests__/itemCommand.test.tsx` "runs one command at a time..." |
| M1 (P0) | replay de compra com chave nova | `useBookingRecovery.test.tsx`, `purchaseFlow.test.tsx` |
| R1 (P0) | `cache.ts` sem `cancelQueries` | `applyConfirmedBooking.test.tsx` "keeps the confirmed balance when a GET that started before the purchase answers late" |

Acumulado de P1: Q15b (borda do cartão com oferta) permanece sobrevivente **equivalente** (só estilo). **Sobreviventes não equivalentes: 0.**

### Isolamento

`git status --porcelain` da árvore real antes do sensor: apenas ` M .specs/features/cicclo-mobile/validation.md`. Depois do sensor, a árvore apareceu com 18 arquivos modificados e 2 novos (`completeSimulation.ts`, `live/simulation.live.test.ts`): são edições de outro agente, que em seguida foram versionadas no commit `ef90f3d` ("add the demo-only conclude action behind an opt-in"). **Não vieram do sensor**: ele rodou só no worktree descartável criado de `fcd69ea`, e a árvore voltou a mostrar só este relatório. Worktree descartável removido, sem `git stash`, reset ou rebase. Nenhum POST.

**Escopo desta verificação:** o PASS vale para `fcd69ea`. O commit `ef90f3d` (ação "concluir" só para demonstração, atrás de opt-in; toca `ItemActions.tsx`, `itemCommand.ts`, `capabilities.ts`, `env.ts`, vários testes e adiciona `completeSimulation.ts` e um teste ao vivo) **chegou durante esta rodada e não foi verificado**. Ele precisa de verificação própria antes de entrar na entrega; uma ação nova sobre itens em execução é código de P1.

## Pendente por dependência externa (à parte; não força FAIL)

Item `queued` e posição reais da fila; corpo real do `cancel` (`refundedAmountCents`, `remainingBalanceCents`) e repetição sem novo crédito; repetições (`confirm` sem reiniciar, mesmo `incidentId`) e 409 em itens cancelados ou em andamento; as três linhas "observado" do `api-contract.md` que repousam em logs perdidos (ativar item já ativo, 410 após o prazo, 202/429 do relato; rerrodar `live/lifecycle.live.test.ts` com a saída em arquivo e aceitando só 410 no `confirm`); compra após horas (retenção da `Idempotency-Key`); compra ao vivo de 2 ou mais unidades e preferência aceita; Android (aceito por decisão do usuário, AD-007, sem ensaio); `pnpm test:bootstrap` e comandos do workspace (sem `dotnet`).

## Resumo (P0 mais P1)

**Overall:** PASS para P0 e P1 em `fcd69ea`, no que se verifica localmente; `ef90f3d` (ação de demonstração) não verificado. **Gaps:** nenhum GAP de AC; observações sem violação (comandos concorrentes no mesmo item, relógio adiantado, lista destacando oferta vencida, texto falado do cartão com oferta vencida). **Mutantes sobreviventes não equivalentes:** nenhum. **Gate:** typecheck 0, lint 0 avisos, 335 de 335 testes, 36 suítes.

---

# Parte P1, rodada 2 (preservada; HEAD `1965e45`, resultado FAIL por Q23)

## Comandos executados (árvore real)

| Comando | Resultado real |
| --- | --- |
| `pnpm --filter @cicclo/mobile typecheck` | exit 0 |
| `pnpm --filter @cicclo/mobile lint` (`--max-warnings 0`) | exit 0, zero avisos |
| `pnpm --filter @cicclo/mobile test` | exit 0: **36 suítes / 36 passaram; 334 testes / 334 passaram; 0 falhas; 0 ignorados** (+12 testes: 5 em `itemCommands.test.tsx`, 2 em `bookingDetails.test.tsx`, 5 casos em `itemCommand.test.tsx`) |
| Grep `test.skip|it.skip|describe.skip|.only(|xit|xdescribe|test.todo|it.todo|TODO|FIXME|fit(|fdescribe` | Nenhuma ocorrência |
| Teste ao vivo; POST à API | Não executados |

## Reavaliação dos quatro GAPs (rodada 1)

Caminhos relativos a `apps/mobile/`; `IC` = `__tests__/itemCommands.test.tsx`, `BD` = `__tests__/bookingDetails.test.tsx`, `IT` = `src/features/bookings/__tests__/itemCommand.test.tsx`.

| ID | Resultado esperado | Evidência nova `file:linha` + asserção | Mutantes | Resultado |
| --- | --- | --- | --- | --- |
| QUEUE-01 | WHERE endpoints comprovados: `confirm` desligada não oferece a ação | `BD:196` `queryByRole('button', { name: /^Confirmar/ })` nulo, com oferta de `expiresAt` 2099 (viva) e o texto `BD:195` "Tempo restante: " presente, o que prova que o botão não some por vencimento | Q14b morto | **COVERED** (era GAP) |
| QUEUE-02 | `queued` mostra a posição só quando a API a envia; já pago | `BD:204` `queryByText(/Posição na fila/)` nulo para `queued` com `queuePosition` nulo, `BD:203` "Na fila" presente; posição presente segue em `IC:113-114,236` | Q19 morto | **COVERED** (era GAP). Item `queued` real da API: PENDING-EXTERNAL |
| QUEUE-04 | No zero: confirmação desabilitada e consulta ao servidor, também no detalhe | `IC:522` `findByText('Oferta vencida')` e `IC:523` `expect(gets).toBeGreaterThan(before)`, medido antes de qualquer tick do polling de 5 s (o teste avança 3,1 s) | Q18 morto | **COVERED** (era GAP) |
| QUEUE-07 | Estorno só com resposta confirmada; carteira atualizada **sem creditar antes** | Com a leitura da carteira **segurada** (`IC:447-453` `holdWallet()`): sucesso `IC:456` `findByText(/Estorno confirmado/)` e `IC:457` `getQueryData(['wallet'])` com `balanceCents: 5000` (valor do `remainingBalanceCents`, não do estorno 1890), sem esperar a leitura; falha 409 `IC:471-472` cache continua 3110; resposta de sucesso inválida `IC:486-487` cache continua 3110 | Q5, Q16, Q25 mortos | **COVERED** (era GAP). Ressalva aberta: Q23, abaixo |

Os 3 mutantes baixos da rodada 1 também morreram: Q13 (`IT:29` `send` chamado uma vez com duas chamadas no mesmo `act`), Q20 (`IT:65` mensagem de 404) e Q17 (`IT:59` `fetch` não chamado para IDs malformados, vazio e `../wallet`).

**P1 agora:** 10 de 10 ACs com evidência; QUEUE-01, 02, 04 e 07 passam de GAP a COVERED; o que só a API real prova está em PENDING-EXTERNAL.

## A mudança de produção em `wallet/cache.ts`

`applyConfirmedBalance` passou de `await queryClient.invalidateQueries(...)` para `void queryClient.invalidateQueries(...)` (`cache.ts:15`), para que a mensagem do cancelamento não espere uma leitura.

| Pergunta | Resposta | Evidência |
| --- | --- | --- |
| A compra e o cancelamento ainda cancelam leituras em voo antes de gravar? | Sim: as duas passam por `applyConfirmedBalance`, que aguarda `cancelQueries(walletKey)` e só depois chama `setQueryData` (`cache.ts:12-13`). R1 (remover `cancelQueries`) morreu | `applyConfirmedBooking.test.tsx` "keeps the confirmed balance when a GET that started before the purchase answers late" |
| Uma falha do `invalidate` vira falha do comando? | Não. `invalidateQueries` não rejeita por falha de leitura (a falha fica no estado da consulta) e a chamada agora é `void`, então nem a espera existe. Se `cancelQueries` ou `setQueryData` lançarem, a compra captura em `applyConfirmedBooking.ts` (`try/catch`), testado ("stays confirmed... when applying the cache throws"). **O cancelamento tem o mesmo `try/catch` em `cancelItem.ts:33-38`, sem teste (Q23 vivo)** | `purchaseTracking.test.tsx` e `applyConfirmedBooking.test.tsx` seguem verdes; Q24 (sem `invalidate`) e Q25 (volta a esperar) mortos |
| O teste do GET atrasado da compra ainda prova o que prova? | Sim. Ele não tem observador ativo, então nunca dependeu do `invalidate` esperado: o que importa é que o GET anterior seja cancelado antes da escrita, e R1 o derruba. O teste novo do cancelamento com a carteira segurada reforça a leitura de que a mensagem não espera a releitura (Q25) |  |

## Os três itens baixos: violam algum AC de P1?

| Item | Veredito |
| --- | --- |
| Dois comandos em voo no mesmo item (confirmar e cancelar, por exemplo) | **Só observação.** Nenhum QUEUE-* trata comandos concorrentes; o servidor serializa e é idempotente para repetir ativar, confirmar e cancelar (OpenAPI). A tela pode mostrar duas mensagens |
| Relógio adiantado esconde "Confirmar" com a oferta ainda válida no servidor | **Não viola.** QUEUE-04 manda desabilitar a confirmação visual quando o contador chega a zero e consultar o servidor, e a spec (tabela de edge cases) diz que o contador é informativo e o 410 prevalece. Agora o detalhe é relido no zero (Q18). O efeito de perda de função com relógio errado é conhecido e aceito pela spec |
| Lista destaca oferta já vencida | **Não viola.** QUEUE-10 pede destacar a oferta e seu prazo; o cartão mostra o contador "Oferta vencida". É só inconsistência de texto falado ("aguardando confirmação até <hora passada>") e fica como observação |

## Sensor da rodada 2 (worktree descartável em `1965e45`)

**Profundidade:** P0/crítico; 28 mutantes injetados, `typecheck` exit 0 em todos.

### Os 9 sobreviventes da rodada 1

| Mutante | Resultado | Teste que matou |
| --- | --- | --- |
| Q5 (credita 9999 numa falha) | **Morto** (2 falhas) | `IC` "leaves the cached balance untouched when the cancellation fails"; "also leaves the balance untouched when a success response is not valid" |
| Q16 (grava o valor estornado como saldo) | **Morto** | `IC` "writes the remaining balance the API returned, not the refunded amount, and does not wait for the wallet read" |
| Q14b (`confirm` ignorada) | **Morto** | `BD` "hides Confirmar for a live offer while the confirm capability is not enabled" |
| Q18 (detalhe não relido no zero) | **Morto** | `IC` "re-reads the detail when the offer deadline reaches zero, before any polling tick" |
| Q19 (`queued` sem posição) | **Morto** | `BD` "does not print a queue position when the API sends none for a queued item" |
| Q13 (sem guarda de duplo toque) | **Morto** | `IT` "runs one command at a time: a second call in the same tick sends nothing" |
| Q20 (404 não distinguido) | **Morto** | `IT` "names each failure by status and errorCode only" |
| Q17 (IDs sem validação) | **Morto** (3 falhas) | `IT` "sends nothing for a malformed booking id / item id / an empty item id" |
| Q15b (borda do cartão) | Sobreviveu, **equivalente** (só estilo; a oferta é destacada também por texto e contador, Q15 morreu) | |

### Amostra do que já morria (P1, 12; P0, 4) e mutantes novos

| Mutante | Descrição | Resultado |
| --- | --- | --- |
| Q1, Q2, Q3, Q4, Q6, Q7, Q8, Q9, Q10, Q12, Q14d, Q21 | confirmar no zero; ativar fora de `pending_activation`; cancelar em `in_progress`; texto de estorno sem checar o valor; relato com estorno; polling sem foco, terminal e 429; `onExpired` a cada render; rótulo falado mudando; capacidade de relatar ignorada; confirmar visível após vencer | Todos **mortos** pelos mesmos testes da rodada 1 |
| M1, N8, R1, R2 | P0: replay com chave nova; botão de compra sem `disabled`; `cache.ts` sem `cancelQueries`; `cache.ts` sem gravar o saldo | Todos **mortos** (R2 agora também pelo teste novo do cancelamento) |
| Q24 | `cache.ts` sem nenhuma invalidação da carteira | Morto (`applyConfirmedBooking.test.tsx` "marks the wallet for refetch"; `purchaseTracking.test.tsx`) |
| Q25 | `cache.ts` volta a aguardar a releitura | Morto (`IC` hardening, carteira segurada) |
| **Q23** | `cancelItem.ts:33-38` sem `try/catch`: uma falha do cache vira falha do cancelamento | **SOBREVIVEU, não equivalente** |

**Q23, o que falta:** em `IC` (cancelamento), fazer `cancelQueries` rejeitar (por exemplo `jest.spyOn(queryClient, 'cancelQueries').mockRejectedValue(new Error('boom'))`) com a resposta 200 válida do cancelamento, e afirmar `findByText(/Estorno confirmado/)` e que "Não foi possível concluir a ação" não aparece. Espelha `applyConfirmedBooking.test.tsx` "stays confirmed... when applying the cache throws". Gravidade baixa: depende de uma exceção do cache, mas o efeito é esconder um estorno que o servidor já fez.

**Resultado do sensor da rodada 2:** 28 injetados, 26 mortos, 2 sobreviveram: Q15b (equivalente) e **Q23 (não equivalente)**.

### Isolamento

`git status --porcelain` da árvore real antes e depois: idêntico (apenas ` M .specs/features/cicclo-mobile/validation.md`, deste relatório). Worktree descartável removido, sem `git stash`, reset ou rebase. Nenhum POST.

## Pendente por dependência externa (à parte; não força FAIL)

Inalterado em relação à rodada 1: item `queued` e posição reais; corpo real do `cancel` e repetição sem novo crédito; repetições (`confirm` sem reiniciar, mesmo `incidentId`) e 409 em itens cancelados ou em andamento; as três linhas "observado" do `api-contract.md` que dependem de logs perdidos (rerrodar `live/lifecycle.live.test.ts` com a saída em arquivo, aceitando só 410 no `confirm` após o prazo); compra após horas; compra de 2 ou mais unidades e preferência aceita; Android (aceito por decisão do usuário, AD-007, sem ensaio); comandos do workspace sem `dotnet`.

## Plano de correção

1. **Q23 (única pendência):** o teste descrito acima. Basta reinjetar Q23.
2. Opcional: rodar `live/lifecycle.live.test.ts` com a saída em arquivo após restaurar a demo.

## Resumo (P0 mais P1)

**Overall:** P0 PASS (sem regressão). P1: 10 de 10 ACs com evidência, 0 GAP, mas **FAIL** pela regra de nenhum mutante não equivalente vivo (Q23). P1 não está entregue enquanto Q23 não for coberto e os itens PENDING-EXTERNAL não forem resolvidos ou aceitos.
**Gate:** typecheck 0, lint 0 avisos, 334 de 334 testes, 36 suítes.
**Sensor:** 26 de 28 mortos; sobrevivente não equivalente: Q23 (baixa).
**Gaps por gravidade:** baixa, Q23 (cancelamento não protegido contra falha de cache, sem teste); observações sem violação de AC: comandos concorrentes no mesmo item, relógio adiantado, lista destacando oferta vencida.


---

# Parte P1, rodada 1 (preservada; `57b40ed..3643c77`, resultado FAIL)

## Comandos executados (árvore real, HEAD `3643c77`)

| Comando | Resultado real |
| --- | --- |
| `pnpm --filter @cicclo/mobile typecheck` | exit 0 |
| `pnpm --filter @cicclo/mobile lint` (`--max-warnings 0`) | exit 0, zero avisos |
| `pnpm --filter @cicclo/mobile test` | exit 0: **35 suítes / 35 passaram; 322 testes / 322 passaram; 0 falhas; 0 ignorados** (+60 testes desde `57b40ed`) |
| `pnpm --filter @cicclo/mobile build` (`expo export`), em worktree descartável | exit 0, bundles iOS e Android; confirma que o `@expo/vector-icons` novo empacota |
| Grep `test.skip|it.skip|describe.skip|.only(|xit|xdescribe|test.todo|it.todo|TODO|FIXME|fit(|fdescribe` | Nenhuma ocorrência |
| Teste ao vivo | **Não executado** (demo com carteira 1020, vários bookings; ele debita e cancela) |

## Critérios de aceite de P1: spec, asserção e evidência

Caminhos relativos a `apps/mobile/`; `IC` = `__tests__/itemCommands.test.tsx`, `PO` = `__tests__/polling.test.tsx`, `OC` = `src/features/bookings/components/__tests__/OfferCountdown.test.tsx`.

| ID | Resultado esperado pela spec | Evidência `file:linha` + asserção | Resultado |
| --- | --- | --- | --- |
| QUEUE-01 | Ativação só em `pending_activation`; confirmação só em `to_be_confirmed` ainda não expirado; WHERE endpoints comprovados | Ativar: `IC:99-102` `getByRole('button',{name:'Ativar wash'})` e `posts[0].url toBe(commandUrl('activate'))`; nunca em `queued`, `to_be_confirmed`, `in_progress`, `completed`, `cancelled`, `failed`: `IC:125-126` `queryByRole(...)` nulo e `posts toHaveLength(0)`. Confirmar vivo: `IC:181-185` `posts[0].url toBe(commandUrl('confirm'))`; expirado: `IC:197-198` botão nulo e `posts` 0; relógio avançado: `IC:209-210`. Capacidades desligadas escondem ativar, cancelar e relatar: `bookingDetails.test.tsx` "offers no action beyond going back while the item commands are not enabled" (mutantes Q14a e Q14c mortos) e `IC:420` para relatar. **`confirm` desligada: sem prova** (o item do teste está vencido, então o botão some por outro motivo); mutante Q14b vivo, `confirmItem.ts:13` | **GAP** (baixa-média) |
| QUEUE-02 | `queued`: posição autoritativa quando presente e explicação de que já está pago | `IC:113-114` `getByText('Posição na fila: 2')` e `getByText('Já pago: o valor foi cobrado na contratação.')`; `IC:236` posição 3. **Posição ausente** (`queuePosition` nulo): sem teste; Q19 vivo (`BookingDetailsScreen.tsx:86` passaria a mostrar "Posição na fila: null"). Item `queued` real da API: nunca observado | **GAP** (baixa) e PENDING-EXTERNAL para o real |
| QUEUE-03 | Contador `max(0, ceil((expiresAt - agora)/1000))`; sem assumir timer em segundo plano | `OC:31-34` `secondsUntil(at(60),T0)` 60, `at(0.2)` 1, `at(0)` 0, `at(-30)` 0; `OC:42-49` "Tempo restante: 60 s", "1 s", "Oferta vencida"; foreground: `OC:70-75` "Tempo restante: 15 s" após `setSystemTime(T0+45000)`. Mutantes Q11 e Q12 mortos | COVERED |
| QUEUE-04 | No zero: confirmação visual desabilitada e consulta ao servidor, sem transicionar localmente | Sem envio no zero: `IC:197-198`; relógio avançado com o timer parado: `IC:209-210` (`posts` 0, botão some; Q1 e Q21 mortos); sem transição local: `IC:182` o status segue "Máquina ofertada" logo após o clique. **Consulta ao servidor**: lista `PO:288-289` `counts.list toBeGreaterThan(before)` e `OC:50` `onExpired` 1 vez; **detalhe**: nenhuma asserção de nova leitura no zero; Q18 vivo (`ItemActions.tsx:29` sem `invalidateQueries`) | **GAP** (baixa-média, só no detalhe) |
| QUEUE-05 | 410 ou 409: atualiza o detalhe e comunica com base no novo estado | 410 com tempo local sobrando: `IC:222` `/A oferta venceu e a execução voltou para a fila/`, `IC:223` "Na fila" vindo do refetch, `IC:224` `gets > before`; 409: `IC:139-141` (ativar), `IC:235-236` (confirmar), `IC:338-340` (cancelar), `IC:399-401` (relatar). O texto "voltou para a fila" é sustentado pelo OpenAPI do `confirm` (devolve a oferta vencida ao fim da fila). Q22 morto | COVERED |
| QUEUE-06 | Cancelar só em `pending_activation`, `queued`, `to_be_confirmed`; nunca em `in_progress`; WHERE suportado | `IC:273` botão em cada um dos três; `IC:281-282` nenhum em `in_progress`, `completed`, `cancelled`, `failed` e `posts` 0. Q3 e Q14c mortos | COVERED |
| QUEUE-07 | Estorno só quando a API o confirma; carteira atualizada, **sem creditá-la antecipadamente** | Texto só com resposta válida: `IC:311` `Execução cancelada. Estorno confirmado: R$ 18,90.`; sem prova na resposta: `IC:325-326` falha e `queryByText(/Estorno confirmado/)` nulo; erro: `IC:338,341` `queryByText(/Estorno/)` nulo; valor zero: `IC:353-354`. Saldo final: `IC:314` `getQueryData(['wallet']) toMatchObject({balanceCents: 5000})`. **Mas** a asserção só é lida depois que a releitura da carteira termina; ela apaga qualquer escrita errada no cache. Mutantes Q5 (credita 9999 numa falha) e Q16 (grava o valor estornado no lugar do saldo restante) sobreviveram | **GAP** (média) |
| QUEUE-08 | Relato aceito mostra `Relato enviado para verificação`, sem afirmar estorno; WHERE implementado | `IC:371` `findByText('Relato enviado para verificação.')`, `IC:374` corpo vazio, `IC:375` `queryByText(/estorno|reembolso|devolv|crédito/i)` nulo, `IC:377` carteira intacta; só em `in_progress`: `IC:387`; 409 `IC:399-401`; 429 `IC:410-411`; capacidade `IC:420`. Q6 e Q14d mortos | COVERED |
| QUEUE-09 | Polling REST seletivo: tela visível, app ativo, online, itens não terminais; suspenso fora disso | Lista 10 s: `PO:97-101`; blur: `PO:110-115`; segundo plano (`focusManager` falso): `PO:123-129` e retomada com releitura; offline: `PO:138-144`; tudo terminal: `PO:155-157`; sem carteira nem POST: `PO:165-170`; 429: `PO:180-184`; desmontar: `PO:193-195`. Detalhe 5 s e para no terminal: `PO:207-215`; ID malformado e segundo plano: `PO:219-229`. Q7, Q8, Q9 e R3 mortos | COVERED |
| QUEUE-10 | Oferta destacada na lista, com prazo, sem exigir abrir o detalhe | `PO:256-258` bloco "Oferta de máquina para wash: confirme no detalhe", "Tempo restante: 60 s" e rótulo falado com "oferta de máquina aguardando confirmação até"; lote misto `PO:275-277`; releitura ao vencer `PO:288-289`; rótulo estático `PO:299`. Q15 morto. A borda destacada (`styles.cardOffer`) é só estilo: Q15b vivo, informativo | COVERED |

**Total P1:** 6 de 10 COVERED, 4 com GAP (QUEUE-01, 02, 04, 07). Nenhum dos GAP exige a API real.

## Revisão adversarial

| Ponto | Achado |
| --- | --- |
| Dois cliques | Cada comando tem um `useRef` síncrono (`itemCommand.ts:44,49`) e o botão fica desabilitado; o teste só cobre o segundo toque depois do re-render (`IC:151-154`, `IC:245-248`). O toque duplo no mesmo quadro não é provado (Q13 vivo). O servidor é idempotente para repetir ativar, confirmar e cancelar (OpenAPI), então o dano é baixo. **Observação:** os quatro comandos têm refs independentes e cada botão só se desabilita por conta própria, então "confirmar" e "cancelar" do mesmo item podem estar em voo ao mesmo tempo; o servidor serializa, mas a tela pode mostrar duas mensagens |
| Confirmar no zero; relógio atrasado ou adiantado | Zero ou vencido no relógio local: nada é enviado e o botão some (`confirmItem.ts:9-10,29`; testes `IC:192-211`). Relógio atrasado (sobra tempo local, servidor já venceu): o servidor decide, 410 tem mensagem e releitura (`IC:213-225`). Relógio adiantado: o botão some mesmo com a oferta ainda válida no servidor, sem possibilidade de confirmar até a oferta mudar; aceitável pela spec (o contador é informativo e o 410 prevalece), mas é uma perda de função por relógio errado, não registrada |
| 410, 409, 404, 429 | 410 e 409 com mensagem e releitura (acima). 429: mensagem com prazo, botão continua utilizável (`IC:405-411`, só para relatar; a função de mensagem é compartilhada). 404: a mensagem "Contratação ou execução não encontrada." não tem teste (Q20 vivo). Todos os comandos invalidam o cache dos bookings em `finally` (`itemCommand.ts:57-60`) |
| Crédito de saldo só com resposta válida | O código é correto: a carteira só é tocada depois de `parseCancelResponse` (`cancelItem.ts:30-34`) e um problema de cache não vira falha. **Os testes não provam**: ver QUEUE-07 (Q5 e Q16 vivos) |
| Texto de estorno sem prova | Só aparece com `refundedAmountCents > 0` da resposta validada (`ItemActions.tsx:83-85`); Q4 morto. O schema do app aceita `refundedAmountCents` 0, enquanto o OpenAPI exige mínimo 1; mais permissivo, inofensivo |
| Relato que menciona estorno | Não: texto fixo (`ItemActions.tsx:98`); Q6 morto |
| Polling em foco, segundo plano, offline, terminal e 429 | Cobertos (QUEUE-09). Item com status desconhecido segue sendo sondado (nunca assumido terminal): decisão coerente com SAFE-01. O polling para após um 429 e só volta com uma leitura bem-sucedida ou foco novo |
| `onExpired` repetido | Uma vez por `expiresAt` (`OfferCountdown.tsx:46-51`); Q10 morto; novo prazo dispara de novo (`OC:100-103`) |
| Vazamento de timers | `setInterval` e o listener do `AppState` são removidos no desmonte (`OC:105-111`); polling não deixa timer (`PO:193-195`); Q10 e Q11 mortos |
| Ação visível com capacidade desligada | Ativar, cancelar e relatar somem (Q14a, Q14c, Q14d mortos). **Confirmar não é provado** (Q14b vivo) |
| Comando para item de outro booking; ID malformado | O caminho usa o `bookingId` da tela e o `itemId` do próprio booking, então a UI não alcança um item de outro booking. `postItemCommand` valida os dois como UUID (`itemCommand.ts:19`), mas nenhum teste cobre (Q17 vivo): equivalente na UI (IDs já validados na rota e no schema), sem teste de unidade |
| Lista destaca oferta vencida | A lista também destaca uma oferta cujo prazo já passou (`BookingsScreen.tsx`, filtro por status e `expiresAt` não nulo, sem checar o relógio), com o texto falado "aguardando confirmação até <hora passada>" ao lado do contador "Oferta vencida". Inconsistência de apresentação, sem teste; baixa |

## Conferência do `api-contract.md` ("Provas P1 contra a API real") com o teste ao vivo, o OpenAPI e o estado real

Leituras `GET` feitas por este Verifier em `127.0.0.1:5001`: OpenAPI, carteira (1020) e lista de contratações (3 bookings).

| Afirmação da tabela | Sustentação encontrada | Avaliação |
| --- | --- | --- |
| Limites por IP e minuto: ativar e confirmar 15 (compartilhado), cancelar 10, relatar 3 | OpenAPI: respostas 429 descrevem "15/min", "10/min" e "3/min" | Sustentada |
| `activate`: 200 `to_be_confirmed` com `machineId` e `expiresAt` (60 s) ao ativar os dois itens do lote | OpenAPI: oferta exclusiva de 60 s. Estado real: o lote tem a secagem `cancelled` e a lavagem `in_progress`, o que exige os dois itens ativados e a lavagem confirmada. O log de "observado diretamente" não foi visto pelo Verifier | Sustentada pelo estado e pelo OpenAPI; o "observado" repousa em log perdido |
| `activate` em item já ativo devolve 200 com o estado atual | OpenAPI: repetir ativar de item ativo devolve o estado | Coerente com o contrato; o log citado não foi visto |
| `confirm`: 410 `MACHINE_OFFER_EXPIRED` logo depois do prazo (observado) | OpenAPI documenta o 410. O teste ao vivo aceita 409 **ou** 410 (`lifecycle.live.test.ts:86-87`); um PASS do teste não distingue os dois. O "410 observado" repousa em log perdido | Não verificável; fraca |
| `confirm`: a lavagem do lote terminou `in_progress` (inferido) | Estado real: lavagem `in_progress` no booking `01a16024` (1890) | Sustentada |
| `cancel`: lavagem A e secagem terminaram `cancelled` (inferido) | Estado real: `01a15fdf` lavagem `cancelled`; `01a16024` secagem `cancelled` | Sustentada |
| `cancel`: saldo 3110, 5000, 1020, 3110 coerente com estornos de 1890 e 2090 | Só a carteira atual (1020, após a compra posterior do usuário: 3110 menos 2090) é verificável; a sequência vem de logs. O OpenAPI diz que o estorno é o preço histórico | Plausível; sequência não verificável |
| `report-issue`: 202 `{incidentId, status}` e 429 na quarta chamada | OpenAPI: 202 e limite 3/min; sem log visto | Coerente; "observado" repousa em log perdido |
| Colunas "Não observado": fila `queued`, corpo do cancel, repetições, mesmo `incidentId`, 409 em cancelado e em andamento | O OpenAPI descreve todos, e o teste ao vivo os exercitaria, mas o resumo se perdeu | Honesto: a tabela não alega mais do que isso |
| Relato do usuário: aceitou oferta e relatou problema pelo app | Estado real: existe o booking `01a16027` (secagem 2090, `in_progress`, criado depois do lote), coerente com compra, ativação e confirmação feitas pelo app. O relato de "relatar problema" não deixa rastro legível na lista | Parcialmente corroborado pelo estado; é relato, sem captura |
| `activate` e `cancel` pelo app: não citados | O doc diz isso | Honesto |

**Veredito da conferência:** a tabela separa corretamente observado, inferido e não observado e não alega o que não tem base, mas **três linhas marcadas "observado diretamente" dependem de logs que ninguém pode reler** (ativar em item ativo, 410 após o prazo, 202/429 do relato); o próprio teste ao vivo tolera 409 ou 410, então a rerrodada com a saída salva em arquivo, prevista pelo doc, é necessária para fechar essas linhas. Isso é PENDING-EXTERNAL, não falha do app. Também não aparecem na tabela: 404 dos comandos, 429 de ativar, confirmar e cancelar, e o fato de o OpenAPI exigir `refundedAmountCents` mínimo 1.

## Sensor de P1 (worktree descartável em `3643c77`)

**Profundidade:** P0/crítico, 34 mutantes. Cada um rodou `typecheck` (exit 0 em todos) e a suíte completa (322 testes).

### P1: mortos

| Mutante | Descrição | Teste que matou |
| --- | --- | --- |
| Q1 | `confirm` envia no zero (`>=`) | `IC` "does not send when the offer ran out while the app was away and the timer never fired" |
| Q2 | ativar oferecido fora de `pending_activation` | `IC` "never offers or sends activate for an item that is cancelled/failed/..." (7 falhas) |
| Q3 | cancelar oferecido em `in_progress` | `IC` "never offers cancel for an item that is in_progress" e "on an error gives no credit..." |
| Q4 | texto de estorno sem checar `refundedAmountCents > 0` | `IC` "shows no refund text when the API credited nothing" |
| Q6 | relato menciona estorno | `IC` "accepts the report: sends an empty POST, says it went for verification, mentions no refund..." |
| Q7 | polling sem a guarda de foco | `PO` "stops when the screen loses focus..." |
| Q8 | polling sem a guarda de terminal | `PO` "does not poll when every booking is terminal"; "refreshes every 5 seconds while the item is not terminal and stops once it is" |
| Q9 | polling sem a guarda de 429 | `PO` "stops polling after the server rate limits the list" |
| Q10 | `onExpired` a cada render | `OC` "treats an already expired timestamp as expired at once and reports it a single time" |
| Q11 | contador sem recálculo no foreground | `OC` "recalculates from timestamps when the app returns to the foreground" |
| Q12 | rótulo falado muda a cada segundo | `OC` "keeps a fixed spoken label..." |
| Q14a, Q14c, Q14d | capacidade ignorada em ativar, cancelar, relatar | `bookingDetails.test.tsx` "offers no action beyond going back..." (Q14a, Q14c); `IC` "hides the action when the capability is not proven" (Q14d) |
| Q15 | oferta não destacada na lista | `PO` "highlights the offer and its deadline..."; "keeps the finished items of a mixed batch..." |
| Q21 | confirmar continua visível após vencer | `IC` (2 falhas) |
| Q22 | mensagem de 409 não mapeada | `IC` (4 falhas) |
| R3, R4 | intervalo do detalhe 1 s; valor do estorno errado no texto | `PO` detalhe 5 s; `IC` "shows the refund the API confirmed..." |

### Regressão de P0 (todos mortos)

R1 (`wallet/cache.ts` sem `cancelQueries`) e R2 (sem gravar o saldo confirmado) por `applyConfirmedBooking.test.tsx`; M1 (replay com chave nova) por `useBookingRecovery.test.tsx` e `purchaseFlow.test.tsx`; N8 (botão de compra sem `disabled`) por `lifecycle.test.tsx` e `purchaseFlow.test.tsx`; L1 (log sem a guarda `__DEV__`) por `client.test.ts`; N1 (`canConfirm` sem `capabilities.purchase`) por `CheckoutProvider.test.tsx`.

### Sobreviventes

| Mutante | Descrição | Arquivo:linha | Classificação | Asserção ou teste que falta |
| --- | --- | --- | --- | --- |
| Q5 | saldo creditado (9999) numa falha de cancelamento | `cancelItem.ts:30` | **Não equivalente** | `IC:338-343` lê o cache depois que a releitura da carteira restaura 3110. Bloquear a releitura (o GET da carteira fica pendente após o comando) e afirmar, logo após a falha, `getQueryData(['wallet'])` igual a 3110 |
| Q16 | cancelamento grava `refundedAmountCents` como saldo | `cancelItem.ts:34` | **Não equivalente** | Mesmo teste: com a releitura bloqueada, afirmar `balanceCents` 5000 (o `remainingBalanceCents` da resposta) logo após o cancelamento |
| Q14b | capacidade `confirm` ignorada | `confirmItem.ts:13` | **Não equivalente** | Em `bookingDetails.test.tsx` (capacidades desligadas) usar uma oferta com `expiresAt` futuro (por exemplo 2099) e afirmar que "Confirmar wash" não existe; o item atual está vencido |
| Q18 | prazo da oferta zera e o detalhe não é relido | `ItemActions.tsx:29` | **Não equivalente** | Em `IC` T33, com `gets` capturado, fazer a oferta chegar a zero e afirmar `gets` maior |
| Q19 | `queued` sem posição mostra "Posição na fila: null" | `BookingDetailsScreen.tsx:86` | **Não equivalente** (baixa) | Item `queued` com `queuePosition` nulo: afirmar que o texto "Posição na fila" não existe e que "Já pago" existe |
| Q13 | comando sem a guarda síncrona de duplo toque | `itemCommand.ts:49` | **Não equivalente** (baixa; servidor idempotente) | Teste de hook: duas chamadas de `run` no mesmo `act`, um único envio |
| Q20 | mensagem de 404 não distinguida | `itemCommand.ts:79` | **Não equivalente** (baixa) | Resposta 404 de um comando: afirmar "Contratação ou execução não encontrada." |
| Q17 | IDs do comando não validados | `itemCommand.ts:19` | Equivalente na UI (IDs já validados na rota e no schema); falta só teste de unidade | `postItemCommand` com `bookingId` ou `itemId` inválido rejeita `InvalidBookingIdError` sem chamar `fetch` |
| Q15b | borda do cartão com oferta removida | `BookingsScreen.tsx` (`styles.cardOffer`) | Equivalente (estilo); a oferta é destacada também por texto e contador | Opcional: afirmar o estilo do cartão |

**Resultado do sensor de P1:** 34 injetados, 25 mortos, 9 sobreviveram (7 não equivalentes, 2 equivalentes). **Sobreviventes não equivalentes: 7.**

### Regressão de P0 desde `57b40ed`

Ícones das abas (`f9a92bc`): `navigation.test.tsx` "gives each tab its own icon without changing the accessible tab names" mantém os nomes `Serviços, tab, 1 of 3` etc.; o export empacota o `@expo/vector-icons` (exit 0). `wallet/cache.ts` (refator de `applyConfirmedBooking`): R1 e R2 mortos, o teste de GET tardio (`applyConfirmedBooking.test.tsx`) segue verde. Teste ao vivo de P0 reaproveita `installNodeFetch` sem mudança de comportamento. Os relatos do usuário sobre o N1 ("Validado o iOS" e "testei todos os cenários aqui e deu tudo certo") e a decisão de que o iOS vale para o Android (AD-007) estão registrados em `native-validation.md` como relato, sem observação por cenário; a decisão não é evidência de ensaio Android. **Nenhum AC de P0 quebrou.**

## Pendente por dependência externa (à parte; não força FAIL)

| Item | Situação |
| --- | --- |
| Item `queued` com `queuePosition` e ordem da fila reais | Nunca observado ao vivo (QUEUE-02) |
| Corpo real de `cancel` (`refundedAmountCents`, `remainingBalanceCents`) e repetição sem novo crédito | Descritos no OpenAPI, não observados; o app só lê o corpo validado |
| Repetições (confirmar sem reiniciar, relatar com o mesmo `incidentId`) e 409 em itens cancelados ou em andamento | Não observados ao vivo (resumo do teste perdido); repetir `live/lifecycle.live.test.ts` com a saída salva em arquivo |
| Linhas "observado" que repousam em log perdido (410 após o prazo; ativar em item ativo; 202/429 do relato) | Dependem de reexecução |
| Compra após horas (retenção da `Idempotency-Key`) | Inferida pelo código do backend; `keyRetentionMs = null` |
| Compra ao vivo de 2 ou mais unidades e preferência aceita | Não exercitada |
| Android (TalkBack, fonte 200%) | Não ensaiado; aceito por decisão do usuário (AD-007), sem ser evidência de ensaio |
| `pnpm test:bootstrap` e comandos do workspace | Falham só por falta de `dotnet` |

## Planos de correção de P1 (só testes; nenhuma mudança de produção)

1. **QUEUE-07 (Q5, Q16):** teste de cancelamento com a releitura da carteira bloqueada: sucesso deixa o cache em 5000 (valor da resposta), falha o deixa em 3110.
2. **QUEUE-01 (Q14b):** oferta futura com `confirm` desligada, sem botão.
3. **QUEUE-04 (Q18):** `gets` aumenta quando o prazo do detalhe zera.
4. **QUEUE-02 (Q19):** `queued` sem posição.
5. Baixa: toque duplo no hook (Q13), mensagem de 404 (Q20), validação de IDs (Q17), e corrigir a lista que destaca oferta vencida como "aguardando confirmação".
6. Rerrodar `live/lifecycle.live.test.ts` com a saída em arquivo depois de restaurar a demo, e ajustar para aceitar só 410 no `confirm` após o prazo.

## Resumo (P0 mais P1)

**Overall:** P0 segue PASS (verificado até `57b40ed`; sem regressão). P1 **não** está pronto: FAIL por 4 ACs com lacuna testável e 7 mutantes não equivalentes vivos, todos de correção barata em teste. P1 não deve ser declarado entregue.

**P1 spec-anchored:** 6 de 10 COVERED (QUEUE-03, 05, 06, 08, 09, 10); 4 com GAP (QUEUE-01, 02, 04, 07).
**Gate:** typecheck 0, lint 0 avisos, 322 de 322 testes, export ok.
**Sensor P1 e regressão:** 25 de 34 mortos; 7 sobreviventes não equivalentes.
**Gaps por gravidade:** média, QUEUE-07 (crédito antecipado não provado); baixa-média, QUEUE-01 (`confirm` desligada) e QUEUE-04 (releitura do detalhe no zero); baixa, QUEUE-02 (fila sem posição), toque duplo, 404; baixa, lista que destaca oferta vencida e três linhas "observado" do doc sobre logs perdidos.


---

# Parte P0 (verificação anterior, preservada; válida até `57b40ed`)

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
