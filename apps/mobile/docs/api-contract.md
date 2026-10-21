# Registro de contratos da API consumidos pelo mobile

**Criado:** 2026-10-21. **Última atualização:** 2026-10-21 (provas contra a API real).

Cada endpoint registra a origem do schema usado em runtime. Enquanto não houver snapshot OpenAPI versionado, a origem é o código da API no commit citado; o schema Zod do módulo consumidor é a validação de fronteira.

| Endpoint | Módulo mobile | Origem do schema | Observações |
| --- | --- | --- | --- |
| `GET /api/v1/wallet` | `src/features/wallet/queries.ts` | `apps/api/src/Wallet/GetBalance/WalletResponse.cs` no commit `a02a845` (branches locais `feat/backend-e0*`) | `balanceCents` inteiro 0..999.999.999.999; `currency` sempre `BRL`; `updatedAt` UTC. Campos extras são ignorados. Gate C1 ainda depende de a API real ser exercitada na demo. |
| `GET /api/v1/services` | `src/features/catalog/queries.ts` | `apps/api/src/Services/GetCatalog/ServicesResponse.cs` no commit `a02a845` | Envelope `{ services: [...] }` ordenado por `code`; `unitPriceCents` inteiro 1..999.999.999.999; `machineType` `washer` ou `dryer`; `canBeBooked` é informativo, a compra revalida no servidor; `unavailabilityReason` é `string` aberta (`SERVICE_DISABLED`, `NO_OPERATIONAL_MACHINE` hoje). Códigos duplicados e `canBeBooked` com motivo são rejeitados. Gate C1 depende da demo com a API real. |
| `POST /api/v1/bookings` | `src/features/checkout/createBooking.ts` | `apps/api/src/Bookings/CreateBooking/CreateBookingRequest.cs`, `CreateBookingEndpoint.cs` e `BookingResponse.cs` no commit `a02a845` | Corpo `{ serviceCodes[1..10], expectedTotalCents, preferredFirstServiceCode? }`; header `Idempotency-Key` UUID. 201 na criação e 200 no replay, ambos `BookingResponse` (saldo atual em `remainingBalanceCents`). Recusas definitivas (nada debitado, chave não consumida): 400 `VALIDATION_ERROR`/`INVALID_SERVICE_SELECTION`, 404 `SERVICE_NOT_FOUND`, 409 `WALLET_INSUFFICIENT_BALANCE`/`PRICE_CHANGED`/`SERVICE_DISABLED`/`NO_OPERATIONAL_MACHINE`, 413, 415. 409 `IDEMPOTENCY_CONFLICT`: a chave pertence a outra intenção. 429: política 5/min com `Retry-After`. 503: resultado não confirmado, repetir com a mesma chave e corpo. Retenção da chave: **não documentada na origem lida**; permanece gate C2. |
| `GET /api/v1/bookings?page=&pageSize=` | `src/features/bookings/listQuery.ts`, `schema.ts` | `apps/api/src/Bookings/ListBookings/ListBookingsEndpoint.cs`, `Platform/Http/Paging.cs` e `BookingResponse.cs` no commit `a02a845` | `PageResponse<BookingResponse>`: `{ items, page, pageSize, hasNextPage }`; padrão 1/20, `pageSize` 1..100; ordem `createdAt` e `bookingId` decrescentes; mesma projeção do detalhe. O mobile carrega páginas por `useInfiniteQuery`, reordena as carregadas e informa `hasMore`. `status` do item é string aberta (rótulo "Estado não reconhecido" para desconhecidos). Gate C3 depende da demo com a API real. |
| `GET /api/v1/bookings/{bookingId}` | `src/features/bookings/detailQuery.ts`, `schema.ts` | `apps/api/src/Bookings/GetBooking/GetBookingEndpoint.cs` e `BookingResponse.cs` no commit `a02a845` | Mesma projeção da listagem, lida num snapshot: preço histórico por item, saldo atual da wallet, estado por item. `bookingId` não UUID responde 400 `VALIDATION_ERROR` (o mobile nem envia o GET). Inexistente: 404 `BOOKING_NOT_FOUND`. Uma oferta vencida aparece com `expiresAt` no passado até a reconciliação. `machineId`, `queuePosition` e `expiresAt` vêm só da API. Gate C3 depende da demo com a API real. |

## Provas contra a API real (gates C1, C2 e C3)

**Data:** 2026-10-21. **API:** `http://127.0.0.1:5001`, base demo recém-preparada (carteira 5000, `wash` 1890, `dry` 2090, nenhuma contratação). **Como reproduzir:** `EXPO_PUBLIC_API_URL=http://127.0.0.1:5001 pnpm --filter @cicclo/mobile test:live`, ver [live/contract.live.test.ts](../live/contract.live.test.ts). O teste usa o transporte, os schemas e a classificação de falhas do próprio app e **debita a carteira demo uma vez** (restaurar a demo antes de repetir). Resultado: 6 testes passam.

| Gate | O que foi observado |
| --- | --- |
| C1 | `GET /wallet` 5000 BRL e `GET /services` com `dry` 2090 e `wash` 1890, ambos contratáveis, validados pelos schemas do app; `GET /bookings` vazio |
| C2 | Sem `Idempotency-Key`: 400 `VALIDATION_ERROR`. Onze códigos: 400 `INVALID_SERVICE_SELECTION`. Código inexistente: 404 `SERVICE_NOT_FOUND`. Total errado: 409 `PRICE_CHANGED`. Duplicatas são precificadas como itens distintos (`wash`,`wash` com total de uma unidade: 409 `PRICE_CHANGED`). Três secagens (6270) com saldo 5000: 409 `WALLET_INSUFFICIENT_BALANCE`. Preferência fora do lote: 400 `INVALID_SERVICE_SELECTION`. Todas classificadas pelo app como `rejected`, saldo intacto em 5000 |
| C2, compra | `POST` de uma lavagem: `chargedAmountCents` 1890, `remainingBalanceCents` 3110, um item `wash` a 1890; o `GET /wallet` seguinte já retorna 3110 (leitura consistente com a escrita). Replay com a mesma chave e o mesmo corpo: mesmo `bookingId`, saldo continua 3110. Mesma chave com outro corpo: 409 `IDEMPOTENCY_CONFLICT`, classificado como `unknown` e bloqueado |
| C2, limite | A sexta chamada de compra em menos de um minuto retorna 429 `RATE_LIMIT_EXCEEDED` com `Retry-After`; o app lê o prazo e classifica como `rejected` (`rate-limited`) |
| C2, retenção da chave | A chave fica gravada no booking com índice único (`ux_service_requests_wallet_idempotency_key`) e o backend não tem limpeza: não há expiração conhecida. O app usa `keyRetentionMs = null`, isto é, o replay vale enquanto o booking existir. O replay após horas não foi exercitado |
| C3 | Depois da compra a listagem tem exatamente esse booking (`page` 1, `pageSize` 20, `hasNextPage` false); o detalhe retorna preço histórico 1890. `GET /bookings/{id}` inexistente: 404 `BOOKING_NOT_FOUND`; id malformado: 400 `VALIDATION_ERROR` (o app nem envia); `page=2&pageSize=1`: página vazia; `page=0&pageSize=500`: 400 |

Observações: logo após a compra o item chegou `to_be_confirmed` com `machineId` e `expiresAt` (havia máquina livre); `queuePosition` é nulo nesse estado. O caso de duas ou mais unidades com compra bem-sucedida e o de preferência aceita não foram exercitados com débito porque o saldo restante (3110) não cobre dois itens; a precificação de duplicatas e a regra da preferência foram provadas pelas recusas acima.

**Ressalvas da prova ao vivo (apontadas pelo Verifier independente):**

1. O código HTTP da criação (201) e do replay (200) não é observado: `createBooking` devolve só o corpo validado. O que o teste prova é o mesmo `bookingId`, o mesmo valor cobrado e a ausência de novo débito. O `remainingBalanceCents` do replay não é afirmado.
2. O 429 foi observado (`RATE_LIMIT_EXCEEDED`, prazo lido de `Retry-After`), mas o código e o formato do cabeçalho só aparecem no log do teste, sem asserção própria.
3. A leitura consistente depois da escrita vem de uma única amostra.
4. Ordenação e paginação com `hasNextPage` verdadeiro não foram vistas ao vivo, pois só existe um booking; estão provadas com API simulada.
5. Só o estado `to_be_confirmed` foi visto ao vivo; os estados terminais e `queued` estão provados com API simulada e pela leitura do backend.
6. A precificação de duplicatas é inferida de uma recusa; nenhuma compra de duas ou mais unidades, nem preferência aceita, foi exercitada com débito. Para fechar isso: restaurar a demo e repetir com `["wash","wash"]` e preferência.
7. O teste ao vivo usa o `fetch` do Node, não o do React Native, e o preço histórico (1890) coincide com o atual. A primeira execução com o `fetch` do React Native contra a API será a N1, no Android.
