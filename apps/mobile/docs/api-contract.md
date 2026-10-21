# Registro de contratos da API consumidos pelo mobile

**Criado:** 2026-10-21. **Última atualização:** 2026-10-21 (T8).

Cada endpoint registra a origem do schema usado em runtime. Enquanto não houver snapshot OpenAPI versionado, a origem é o código da API no commit citado; o schema Zod do módulo consumidor é a validação de fronteira.

| Endpoint | Módulo mobile | Origem do schema | Observações |
| --- | --- | --- | --- |
| `GET /api/v1/wallet` | `src/features/wallet/queries.ts` | `apps/api/src/Wallet/GetBalance/WalletResponse.cs` no commit `a02a845` (branches locais `feat/backend-e0*`) | `balanceCents` inteiro 0..999.999.999.999; `currency` sempre `BRL`; `updatedAt` UTC. Campos extras são ignorados. Gate C1 ainda depende de a API real ser exercitada na demo. |
| `GET /api/v1/services` | `src/features/catalog/queries.ts` | `apps/api/src/Services/GetCatalog/ServicesResponse.cs` no commit `a02a845` | Envelope `{ services: [...] }` ordenado por `code`; `unitPriceCents` inteiro 1..999.999.999.999; `machineType` `washer` ou `dryer`; `canBeBooked` é informativo, a compra revalida no servidor; `unavailabilityReason` é `string` aberta (`SERVICE_DISABLED`, `NO_OPERATIONAL_MACHINE` hoje). Códigos duplicados e `canBeBooked` com motivo são rejeitados. Gate C1 depende da demo com a API real. |
