# Registro de contratos da API consumidos pelo mobile

**Criado:** 2026-10-21. **Última atualização:** 2026-10-21.

Cada endpoint registra a origem do schema usado em runtime. Enquanto não houver snapshot OpenAPI versionado, a origem é o código da API no commit citado; o schema Zod do módulo consumidor é a validação de fronteira.

| Endpoint | Módulo mobile | Origem do schema | Observações |
| --- | --- | --- | --- |
| `GET /api/v1/wallet` | `src/features/wallet/queries.ts` | `apps/api/src/Wallet/GetBalance/WalletResponse.cs` no commit `a02a845` (branches locais `feat/backend-e0*`) | `balanceCents` inteiro 0..999.999.999.999; `currency` sempre `BRL`; `updatedAt` UTC. Campos extras são ignorados. Gate C1 ainda depende de a API real ser exercitada na demo. |
