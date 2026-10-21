# Cicclo mobile

**Criado:** 2026-10-21. **Última atualização:** 2026-10-21.

App Expo (React Native) da lavanderia Cicclo. Mostra o saldo da carteira demo, o catálogo de serviços, permite montar uma seleção sem debitar, revisar e contratar com idempotência, e acompanhar as contratações. As máquinas são simuladas e a cobrança é imediata, mesmo quando o serviço entra em fila.

## Estado desta entrega

| Área | Estado |
| --- | --- |
| Navegação, header, catálogo, seleção, revisão (E1 e E2) | Implementado e coberto por testes com API fake |
| Contratação com intenção única, verificação e recuperação (E3) | Implementado e provado contra a API real (gate C2). O botão de contratar está ligado (`capabilities.purchase = true`) |
| Lista, detalhe, revalidação por foco e rede (E4) | Implementado e provado contra a API real (gate C3) |
| Validação nativa (N1) | **Aprovada pelo usuário no iOS** em 2026-10-21, em relato global ("Validado o iOS"), sem registro por cenário; o Android não foi ensaiado. Ver [docs/native-validation.md](docs/native-validation.md) |
| Ciclo de vida do serviço: ativar, confirmar oferta, cancelar, relatar problema, polling (P1) | **Não implementado.** Só começa depois de P0 verificado e de cada endpoint comprovado |
| Intenção persistida, SignalR, APK (P2) | Fora do escopo |

Os contratos foram lidos do código do backend e **provados contra a API real em 2026-10-21** (leituras, compra com replay e conflito, recusas, 429, lista e detalhe), com o código do próprio app; evidência e como repetir em [docs/api-contract.md](docs/api-contract.md). O que ainda não foi feito é a validação no Android (N1).

## Instalação

Requisitos: Node 22 (`.nvmrc` na raiz) e pnpm 10. Na raiz do repositório:

```sh
nvm use
pnpm install --frozen-lockfile
```

Configuração do app (arquivo `apps/mobile/.env`, a partir de [.env.example](.env.example)):

| Variável | Uso |
| --- | --- |
| `EXPO_PUBLIC_API_URL` | Origem da API, sem caminho, query ou credenciais. O app acrescenta `/api/v1`. Obrigatória: sem ela o app não usa `localhost` por conta própria e mostra erro nas leituras |
| `EXPO_PUBLIC_DEMO_DISPLAY_NAME` | Nome da saudação. Vazio ou ausente mostra `Visitante` |

URL da API por dispositivo (a API de desenvolvimento usa a porta 5001):

| Onde o app roda | Valor |
| --- | --- |
| Simulador iOS | `http://localhost:5001` |
| Emulador Android | `http://10.0.2.2:5001` |
| Aparelho físico | `http://<IP da máquina na rede local>:5001`, na mesma rede |

Variáveis `EXPO_PUBLIC_*` são embutidas no app e não são secretas. Nunca coloque credenciais nelas.

Para rodar no simulador iOS: copie `.env.example` para `.env` (`cp apps/mobile/.env.example apps/mobile/.env`), com a API de pé em `http://localhost:5001`, e inicie com `pnpm --filter @cicclo/mobile ios`. O Expo lê o `.env` **ao iniciar o Metro**: se o arquivo for criado ou alterado depois, pare o Metro e inicie de novo (`--clear` se o valor antigo persistir). Sem `EXPO_PUBLIC_API_URL`, o header, a Home, Serviços e o detalhe mostram "Configuração da API ausente ou inválida. Defina EXPO_PUBLIC_API_URL em apps/mobile/.env e reinicie o Metro." Em desenvolvimento, falhas de requisição também aparecem no terminal do Metro como `[api] GET /wallet failed` com tipo, status, código e traceId, sem corpo, cabeçalhos ou chave de idempotência.

## Executar

```sh
pnpm --filter @cicclo/mobile dev       # Expo (porta 8081)
pnpm --filter @cicclo/mobile android   # abre no Android
pnpm --filter @cicclo/mobile ios       # abre no iOS
```

Subir a API e a base demo é responsabilidade do backend (ver o README da raiz e o roteiro do backend).

## Fluxo do usuário

1. **Home** (aba central e inicial): lista os serviços com preço e disponibilidade vindos da API. Adicionar e remover unidades monta a seleção, sem debitar: com saldo 5000 e lavagem 1890 mais secagem 2090, o app mostra selecionado 3980 e disponível 1020, e o header continua em 5000.
2. **Checkout**: revisa quantidades, preferência de execução, subtotal, saldo confirmado e saldo estimado, avisa que a cobrança é imediata e que as máquinas são simuladas, e contrata. O botão só habilita com dados confirmados, sem problemas na seleção e com os preços revisados.
3. **Serviços**: lista as contratações da carteira (Em andamento e Histórico) e abre o detalhe com preço histórico e estado por item.

## Como o estado é organizado

- **Código por domínio** em `src/features/{wallet,catalog,home,checkout,bookings}`, com o que é compartilhado em `src/shared`. `src/app` contém só rotas finas.
- **TanStack Query** guarda o estado remoto (carteira, catálogo, contratações). Não existe segunda cópia do saldo em contexto.
- **Seleção** (`checkout`): reducer puro mais seletores em centavos inteiros. Total e saldo disponível ficam `null`, nunca zero, quando falta dado.
- **Provider do checkout** fica acima das abas: a seleção e a contratação em andamento sobrevivem à troca de abas.
- **Validação nas fronteiras** com Zod. Resposta que quebra o contrato nunca vira valor de fallback.

## Contratação, idempotência e recuperação

- Cada intenção de compra recebe uma `Idempotency-Key` UUID v4 e um corpo serializado uma única vez. Qualquer reenvio dessa intenção usa exatamente a mesma chave e os mesmos bytes.
- Dois toques antes do próximo render geram no máximo um envio (guarda síncrona no comando, não só no botão).
- Resultado desconhecido (timeout, perda de rede, 5xx, resposta de sucesso inválida) fica em `unknown`: o app não declara sucesso nem falha e oferece **Verificar contratação**, que reenvia a mesma chave e o mesmo corpo. Não há reenvio automático, nem ao reconectar nem ao voltar do segundo plano.
- Recusas definitivas conhecidas (saldo insuficiente, preço alterado, serviço indisponível e outras da lista do contrato) viram `rejected`, sem cobrança. Preço alterado exige aceitar os novos preços antes de nova tentativa, com nova chave.
- `IDEMPOTENCY_CONFLICT` bloqueia a recuperação e não libera nova compra. `429` respeita o prazo de `Retry-After`.
- Confirmada a compra, o saldo retornado entra no cache depois de cancelar leituras de carteira em voo, para que uma resposta antiga não o sobrescreva; carteira e contratações são invalidadas.
- Offline, ou enquanto relê após voltar do segundo plano ou reconectar, o saldo aparece como desatualizado e adições e compra ficam bloqueadas até carteira e catálogo serem relidos com sucesso.

## Limitações conhecidas

- **Reiniciar o app perde a intenção em andamento.** A chave não é persistida; a recuperação entre reinícios exige a opção P2 (intenção persistida), fora do escopo. Depois de um reinício, consulte a aba Serviços antes de decidir contratar de novo.
- **Retenção da chave de idempotência:** o backend grava a chave junto da contratação, com índice único e sem limpeza; não há expiração conhecida, então o app não bloqueia a verificação por idade (`keyRetentionMs = null`). O replay depois de horas não foi exercitado.
- **O app não assume uma URL de API.** Sem `apps/mobile/.env` (ou sem reiniciar o Metro depois de criá-lo), nenhuma leitura funciona e o app mostra a instrução de configuração acima, em vez de usar `localhost` por conta própria.
- Não há polling: a atualização acontece ao abrir telas, ao voltar do segundo plano, ao reconectar e por ações explícitas.
- A lista de contratações é paginada; os filtros valem para os registros carregados, e a tela avisa quando existem mais.

## Qualidade: comandos e resultados

Comandos na raiz. Resultados reais de 2026-10-21 (commit de documentação deste arquivo; contagens atuais no histórico Git):

| Comando | Resultado |
| --- | --- |
| `pnpm --filter @cicclo/mobile typecheck` | Passa |
| `pnpm --filter @cicclo/mobile lint` | Passa, zero avisos |
| `pnpm --filter @cicclo/mobile test` | Passa: 32 suítes, 260 testes |
| `pnpm --filter @cicclo/mobile build` | Passa: `expo export` para Android e iOS |
| `pnpm typecheck` (workspace) | Passa |
| `pnpm lint`, `pnpm test`, `pnpm build` (workspace) | **Falham por motivo externo**: o pacote da API exige `dotnet`, que não está no PATH da máquina onde foram executados. As partes mobile passam isoladamente (linhas acima) |
| `pnpm test:bootstrap` | 10 de 11 passam. Falha o teste que compila a API em Release, também por falta de `dotnet` |

Os testes mobile usam uma API simulada no limite de `fetch`: `pnpm test` nunca acessa a rede. A verificação contra a API real é opt-in e **debita a carteira demo uma vez** (restaure a demo antes de repetir):

```sh
EXPO_PUBLIC_API_URL=http://127.0.0.1:5001 pnpm --filter @cicclo/mobile test:live
```

Um export bem-sucedido não prova que o app funciona em um aparelho.

## Mapa de capacidades

| Capacidade | Estado | Prova |
| --- | --- | --- |
| Carteira, catálogo, seleção, revisão | Funcional | Testes de integração e prova contra a API real (C1) |
| Contratação com idempotência e recuperação | Funcional | Testes de integração e prova contra a API real (C2) |
| Lista e detalhe de contratações | Funcional | Testes de integração e prova contra a API real (C3) |
| Ativar, confirmar oferta, cancelar, relatar, polling | Não implementado | Fora de P0 |
| App no iOS (gate N1) | Aprovado pelo usuário em relato global, sem registro por cenário | Relato do usuário; Android sem ensaio |
