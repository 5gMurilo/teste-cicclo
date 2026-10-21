# Cicclo

Monorepo pnpm + Turborepo com dois aplicativos independentes:

- `apps/mobile`: React Native 0.88.0, Expo SDK 58 e TypeScript.
- `apps/api`: C# e ASP.NET Core em .NET 10 LTS, com endpoint `GET /health`.

O workspace reconhece `apps/*` e `packages/*`. Não há pacotes compartilhados ainda; crie-os apenas quando houver código realmente compartilhado. Expo/React Native não oferecem uma linha LTS formal: o mobile usa a versão estável compatível do Expo. O .NET 10 tem suporte LTS até novembro de 2028.

## Pré-requisitos

| Ferramenta | Versão |
| --- | --- |
| Node.js LTS | 22.22.3 (`.nvmrc`) |
| pnpm | 10.33.0 (`packageManager`) |
| SDK .NET | 10.0.402 (`global.json`, permite patches da mesma feature band) |
| Docker + Compose v2 | Necessários apenas para os containers |

Para abrir o mobile, use Expo Go compatível com SDK 58 em dispositivo Android/iOS, um Android Emulator ou um iOS Simulator. Emuladores exigem Android Studio/SDK; o simulador iOS exige macOS com Xcode. Dispositivos físicos precisam alcançar o servidor Expo na rede local. Builds nativos locais exigem também as ferramentas da plataforma; não são necessários para exportar os bundles.

O pnpm gerencia somente dependências JavaScript. O SDK .NET e o NuGet continuam responsáveis pela compilação e pelas dependências C#; o `package.json` da API apenas permite que o Turbo execute os comandos nativos.

## Instalação

Com as versões acima disponíveis no PATH, execute na raiz:

```sh
nvm use
pnpm install --frozen-lockfile
dotnet restore apps/api/Cicclo.Api.slnx --locked-mode
```

`nvm use` é opcional se o Node correto já estiver selecionado. O repositório não instala ferramentas globais. Mantenha `pnpm-lock.yaml` e os `packages.lock.json` do NuGet versionados; atualize-os deliberadamente ao mudar dependências.

## Desenvolvimento

```sh
pnpm dev
```

O Turborepo inicia Expo (porta 8081) e a API (`http://localhost:5001`) em paralelo. A porta 5001 evita conflito com o AirPlay do macOS na 5000. O mobile mostra `Cicclo`, sem chamar a API ou depender do banco. Pare o desenvolvimento com `Ctrl+C`.

A API usa `dotnet watch --no-hot-reload`: alterações reiniciam o processo. Isso evita um erro de leitura do manifest de static web assets observado no hot reload do SDK 10.0.402 no macOS; a API não serve arquivos estáticos nesta base.

Execução individual pela raiz:

```sh
pnpm --filter @cicclo/mobile dev
pnpm --filter @cicclo/mobile android
pnpm --filter @cicclo/mobile ios
pnpm --filter @cicclo/api dev
```

No terminal do Expo, também é possível ler o QR code pelo Expo Go, pressionar `a` para Android ou `i` para iOS. Não há aplicação web nesta base.

Diagnóstico da API:

```sh
curl http://localhost:5001/health
curl http://localhost:5001/health/live
curl http://localhost:5001/health/ready
```

`/health` (alias legado) e `/health/live` respondem HTTP 200, `text/plain`, corpo `Healthy`: liveness do processo, sem consultar o PostgreSQL. `/health/ready` responde 200 `Healthy` somente quando o banco está acessível e a base demo foi preparada pelo bootstrap (wallet demo, catálogo e máquinas presentes); caso contrário, 503 `Unhealthy`, sem detalhes do banco. Saldo, preços e estados alterados pelo uso não afetam a readiness. Não há autenticação. A conexão `ConnectionStrings:Cicclo` é resolvida apenas quando um componente de dados a usa; sem ela, a API continua subindo.

Em `Development`, o documento OpenAPI 3.1 gerado pelos endpoints reais fica em `GET /openapi/v1.json` (por exemplo, `curl http://localhost:5001/openapi/v1.json`). Ele lista somente operações já entregues e não é publicado em outros ambientes.

### Consultas da demonstração

Depois do bootstrap (seção "Seed e volume"), a API lê os dados autoritativos do PostgreSQL a cada chamada:

```sh
curl http://localhost:5001/api/v1/wallet
curl http://localhost:5001/api/v1/services
```

`GET /api/v1/wallet` responde 200 `application/json` com `{ "balanceCents": 5000, "currency": "BRL", "updatedAt": "<UTC>" }` na base recém-preparada. Valores monetários são centavos inteiros (int64) convertidos do `numeric(12,2)` sem arredondamento. A wallet é resolvida no servidor: parâmetros como `walletId` ou `userId` são ignorados e não trocam a conta.

`GET /api/v1/services` responde `{ "services": [...] }` ordenado por `code` (a ordem não é prioridade de execução). Na base recém-preparada: `dry` (Secagem, `dryer`, 2090 centavos) e `wash` (Lavagem, `washer`, 1890 centavos), ambos com `canBeBooked: true` e `unavailabilityReason: null`. Cada item traz `code`, `name`, `machineType`, `unitPriceCents`, `currency`, `isActive`, `canBeBooked` e `unavailabilityReason`, sempre presente:

- serviço inativo: `canBeBooked: false` e `SERVICE_DISABLED`, mesmo sem máquina operacional;
- serviço ativo sem máquina do tipo em `available`, `reserved` ou `running`: `canBeBooked: false` e `NO_OPERATIONAL_MACHINE`;
- máquinas reservadas ou em execução continuam contando como capacidade operacional.

`canBeBooked` é **informativo**: o futuro comando de compra revalidará serviço, capacidade e preço no servidor no momento da contratação. A API ainda não oferece compra nesta etapa. Wallet e catálogo compartilham o mesmo limite de leituras.

Erros usam `application/problem+json` com `type` `urn:cicclo:problem:<slug>`, `errorCode`, `traceId` e `errors` (null quando não há erro de campo):

| Status | errorCode | Quando |
| --- | --- | --- |
| 429 | `RATE_LIMIT_EXCEEDED` | Mais de 60 leituras por minuto do mesmo IP; o cabeçalho `Retry-After` informa os segundos de espera. |
| 503 | `DATABASE_UNAVAILABLE` | Conexão não configurada, PostgreSQL inacessível ou base demo não preparada. Nenhum saldo é inventado. |
| 500 | `INTERNAL_ERROR` | Falha inesperada; o corpo não expõe SQL, stack trace ou dados de conexão. |

O limite de leituras é uma janela fixa de 1 minuto por endereço IP remoto, sem fila, configurável por `RateLimiting:Reads:PermitLimit` (padrão 60). Cabeçalhos como `X-Forwarded-For` não são considerados. Os endpoints de saúde não entram nesse limite.

### Observabilidade

A API escreve logs estruturados em JSON no console (um único provider), com `TraceId` e `SpanId` nos escopos. Um cabeçalho W3C `traceparent` recebido é continuado pelo trace da requisição. Traces e métricas HTTP vêm da instrumentação nativa do ASP.NET Core: as métricas usam rota normalizada (`/api/v1/wallet`), método e status, nunca IDs ou query string. O recurso identifica `service.name=cicclo.api`, `service.version` (versão do assembly, com commit quando disponível no build) e `deployment.environment.name`.

| Configuração | Padrão | Efeito |
| --- | --- | --- |
| `OpenTelemetry:Enabled` | `true` | `false` desliga traces, métricas e exportação; os logs JSON no console continuam. |
| `OpenTelemetry:OtlpEndpoint` | ausente | URL base http(s) de um coletor OTLP/HTTP, por exemplo `http://127.0.0.1:4318/`. Sem ela nada é exportado, nem para `localhost`. Valor inválido impede o startup. |

Com endpoint, logs, traces e métricas são enviados em protobuf para `v1/logs`, `v1/traces` e `v1/metrics` sob a URL base, em background, com filas limitadas (2048 itens) e timeout de 5 s por envio. Ao parar, a API envia o último lote com o mesmo prazo. Coletor fora do ar ou lento não altera as respostas nem a saúde. Exemplo com variáveis de ambiente: `OpenTelemetry__OtlpEndpoint=http://127.0.0.1:4318/`. Nenhum coletor ou dashboard faz parte do projeto. Ainda não há spans ou métricas de compra ou SQL.

O meter `Cicclo.Api.Laundry` publica `cicclo_queue_wait_seconds` (histograma da espera entre `queued_at` e a oferta da máquina) e `cicclo_offers_expired_total` (ofertas vencidas devolvidas à fila), ambos só com a dimensão `machine_type` (`washer` ou `dryer`) e registrados somente depois do commit da transação que produziu o fato.

### Reconciliação de ofertas

No modo API normal, um worker em background reconcilia a fila a cada `Laundry:QueueWorkerIntervalSeconds` (padrão 5 s). Cada iteração abre um escopo e uma transação própria, bloqueia wallet, máquinas e itens nessa ordem, devolve ao fim da fila as ofertas vencidas (`now >= expires_at`, ticket e `queued_at` novos, sem débito nem cancelamento) e oferta as máquinas livres aos próximos por ticket, com prazo de `Laundry:ConfirmationTimeoutSeconds` (padrão 60 s). O estado vive só no PostgreSQL: reiniciar a API não perde fila nem oferta, e a primeira iteração após o reinício reconcilia o que venceu enquanto ela estava parada. Uma iteração que falha é registrada em log (evento 320) e a próxima tentativa só ocorre no intervalo seguinte; ao parar, a espera e o SQL em andamento são cancelados e a transação é revertida. O prazo da oferta não depende do worker: uma confirmação com `now >= expires_at` já é recusada e reconcilia a fila na mesma transação. O modo `--bootstrap-demo` não inicia o worker. Ainda não há endpoints de ativação ou confirmação.

## Validação

```sh
pnpm build
pnpm lint
pnpm typecheck
pnpm test
pnpm test:bootstrap
```

- `build`: exporta bundles de Android e iOS para `apps/mobile/dist/` e compila a solução .NET em Release. **Não gera APK/IPA**, não assina e não publica aplicativos. Builds nativos e EAS ficam fora desta inicialização.
- `lint`: ESLint no mobile e `dotnet format whitespace --verify-no-changes` na API, sem alterar fontes.
- `typecheck`: TypeScript no mobile; os tipos C# são verificados pela compilação da API em `build`.
- `test`: teste de renderização da tela mobile e teste HTTP de integração `/health`, sem modo watch.
- `test:bootstrap`: contratos do workspace, documentação e infraestrutura; execute depois de `build`, pois também verifica os artefatos gerados.

As tarefas .NET não usam cache Turbo nesta base. O build do mobile pode restaurar `dist/` do cache; desenvolvimento e testes não usam cache. Os comandos da raiz preservam código de saída diferente de zero quando uma tarefa falha.

O repositório não contém um script `scripts/smoke.mjs`; não há provas adicionais versionadas além dos comandos acima. Para o backend, `apps/api/scripts/start-demo.sh` sobe banco, bootstrap e API em containers e confere as consultas (seção "Demonstração local completa").

## PostgreSQL local com Compose

O Compose em `apps/api/docker-compose.yml` define o banco `postgres` e, no perfil `app`, os serviços `bootstrap` e `api`, usados pelo script da seção "Demonstração local completa". Sem perfil, os comandos abaixo sobem **apenas o banco**. Prepare uma senha local:

```sh
cp apps/api/.env.example apps/api/.env
```

Edite `apps/api/.env`, substituindo `POSTGRES_PASSWORD`, e depois execute na raiz:

```sh
docker compose -f apps/api/docker-compose.yml --env-file apps/api/.env up -d --wait
docker compose -f apps/api/docker-compose.yml --env-file apps/api/.env logs postgres
docker compose -f apps/api/docker-compose.yml --env-file apps/api/.env exec postgres psql -U cicclo -d cicclo
```

Conexão: `127.0.0.1:5432`, banco e usuário `cicclo`, senha definida no `.env`. A porta fica restrita ao loopback; altere `POSTGRES_PORT` se 5432 já estiver ocupada. Nunca use a senha de exemplo em produção. Arquivos `.env` são ignorados pelo Git. Se a senha tiver `$`, aspas ou outros caracteres especiais, escreva-a entre aspas simples no `.env` (`POSTGRES_PASSWORD='...'`): o Compose lê o valor literalmente.

### Seed e volume

O PostgreSQL inicia vazio. As tabelas vêm das migrations do EF Core e `apps/api/init/seed.sql` contém **somente dados iniciais**: wallet BRL com saldo 50.00, serviços `wash` (Lavagem, 18.90) e `dry` (Secagem, 20.90) e as máquinas `washer-01` e `dryer-01`, todas com identificadores estáveis. O arquivo não é montado em `/docker-entrypoint-initdb.d`: ali ele rodaria antes das migrations e apenas em volume novo.

As migrations também criam o histórico financeiro das compras, que o seed nunca preenche: `service_requests` (compra confirmada, única por wallet e chave de idempotência, total em `numeric(12,2)`), `service_request_items` (uma linha por execução contratada, com o preço histórico cobrado) e `wallet_transactions` (lançamentos de valor positivo e saldo resultante não negativo; no máximo um débito por compra). As chaves estrangeiras são `ON DELETE RESTRICT`: apagar serviço, wallet ou compra com histórico é recusado. A API ainda não expõe comando de compra.

Para a fila e as ofertas de máquina, cada item guarda `queue_ticket` (da sequência monotônica `queue_ticket_seq`; lacunas deixadas por transações revertidas são normais), `queued_at` (última entrada na fila, base da medida de espera), `machine_id`, `offered_at`, `expires_at` e `started_at`, e cada máquina guarda `active_item_id`. O banco recusa estados incompatíveis: item pendente sem ticket, oferta sem máquina ou com prazo não posterior à oferta, início fora da validade da oferta, máquina `reserved` sem item ou livre/fora de serviço com item. Um índice único parcial garante no máximo uma atribuição ativa (`to_be_confirmed` ou `in_progress`) por máquina. Essas colunas ficam nulas nas compras já existentes.

Para preparar o banco, execute o modo explícito `--bootstrap-demo` da própria API, com o banco do Compose já ativo:

```sh
export ASPNETCORE_ENVIRONMENT=Development
export Laundry__DemoMode=true
export ConnectionStrings__Cicclo='Host=127.0.0.1;Port=5432;Database=cicclo;Username=cicclo;Password=<senha do .env>'
dotnet run --project apps/api/src/Cicclo.Api.csproj --no-launch-profile -- --bootstrap-demo
```

O comando aplica as migrations pendentes e, somente depois do sucesso, executa o `seed.sql` em uma transação. Ele não abre listener HTTP, não usa `psql` nem ferramentas globais e não imprime a connection string. A imagem Docker da API aceita a mesma opção e as mesmas variáveis.

| Código de saída | Significado |
| --- | --- |
| 0 | Migrations e seed concluídos. |
| 1 | Migration falhou (seed não executado) ou seed falhou (todos os inserts do seed revertidos; migrations aplicadas são mantidas). |
| 2 | Recusado: ambiente diferente de Development, `Laundry:DemoMode` ausente/false ou `ConnectionStrings:Cicclo` não configurada. |

Repetir o comando é seguro: os inserts usam `ON CONFLICT ... DO NOTHING` sobre as identidades estáveis e preservam saldo, preços, estados e datas já existentes. Nada é apagado nem restaurado. Se outra máquina já usar o nome `washer-01` ou `dryer-01` com outro UUID, o seed falha sem alterar dados; investigue antes de repetir. A API em modo normal nunca executa migrations nem seed, e reiniciá-la não restaura a demonstração.

O volume nomeado `postgres-data` é montado em `/var/lib/postgresql`, o caminho recomendado para PostgreSQL 18+. Para parar sem perder dados:

```sh
docker compose -f apps/api/docker-compose.yml --env-file apps/api/.env down
```

Recriar o container preserva o banco. Remover o volume (`down --volumes`) **apaga os dados locais**; não faça isso para simplesmente reiniciar serviços. Migrations e seed usam transações separadas: se o seed falhar depois das migrations, a estrutura permanece e o bootstrap pode ser repetido após corrigir o problema. Alterar a senha no `.env` também não altera automaticamente a senha de um usuário em um volume já inicializado.

## Demonstração local completa

Pré-requisitos: Docker com Compose v2 (`docker compose`), daemon ativo e `curl` no host. O SDK .NET não é necessário: a API roda na imagem do `Dockerfile`. Com `apps/api/.env` preparado como na seção anterior, execute na raiz:

```sh
apps/api/scripts/start-demo.sh
```

O script, em ordem:

1. confere pré-requisitos, o arquivo `.env` e a configuração do Compose, sem instalar nada e sem executar o `.env` como código;
2. sobe o PostgreSQL e aguarda o healthcheck;
3. constrói a imagem da API;
4. executa um bootstrap **novo** a cada chamada (`docker compose run --rm bootstrap`: migrations e depois `seed.sql`) e interrompe tudo se ele falhar;
5. inicia a API somente depois do bootstrap concluído;
6. aguarda `GET /health/ready` com prazo finito e confere `GET /api/v1/wallet` e `GET /api/v1/services`, imprimindo as respostas.

Na base nova, a saída mostra `balanceCents` 5000 e os preços 2090 (`dry`) e 1890 (`wash`). Repetir o script preserva saldo, preços e estados já alterados: o seed só insere o que falta. A demo fica em `http://127.0.0.1:5001` (porta `API_PORT` do `.env`), com OpenAPI em `/openapi/v1.json`. Ainda não há compra; o catálogo só informa se o serviço seria contratável.

| Código de saída | Significado |
| --- | --- |
| 0 | Banco, bootstrap e API prontos; smoke das consultas aprovado. |
| 1 | Falha de execução: Compose, bootstrap (a API não é iniciada e nenhum dado é apagado), readiness fora do prazo ou smoke. |
| 2 | Pré-requisito ou configuração ausente: `docker`/`curl`, Compose v2, daemon, `.env`, `POSTGRES_PASSWORD` ou prazo inválido. |

Variáveis opcionais do script: `CICCLO_ENV_FILE` (outro arquivo de ambiente), `CICCLO_READY_TIMEOUT` (segundos de espera pela readiness, padrão 60) e `COMPOSE_PROJECT_NAME` (outro projeto Compose, com volume próprio). No `.env`, `API_PORT` muda a porta da API e `CICCLO_OTLP_ENDPOINT` habilita a exportação OTLP descrita em "Observabilidade" (o endereço precisa ser alcançável de dentro do container, por exemplo `http://host.docker.internal:4318/` no Docker Desktop).

Conexões: no host, ferramentas usam `127.0.0.1:${POSTGRES_PORT}`; dentro do Compose, bootstrap e API usam `Host=postgres;Port=5432`. A senha chega ao Npgsql pela variável `PGPASSWORD` do container, como dado, e não aparece na connection string, na imagem, no `seed.sql` nem na saída do script. Banco e API publicam portas somente no loopback, e a API não habilita CORS.

Falhas comuns: senha alterada no `.env` depois de criar o volume não muda a senha já gravada no banco (o bootstrap falha com erro de autenticação); outra máquina com o nome `washer-01` ou `dryer-01` faz o seed falhar sem alterar dados; readiness fora do prazo indica API sem acesso à base (consulte `docker compose -f apps/api/docker-compose.yml --env-file apps/api/.env logs api`). O script nunca remove volumes. Para parar, use `docker compose -f apps/api/docker-compose.yml --env-file apps/api/.env --profile app down`, sem `--volumes`.

## Imagem Docker da API

Na raiz, use `apps/api` como contexto de build:

```sh
docker build -t cicclo-api:local apps/api
docker run --rm -p 127.0.0.1:5001:8080 cicclo-api:local
```

O Dockerfile usa build multi-stage com SDK .NET fixado e runtime ASP.NET Core não-root. A imagem expõe a porta 8080 e responde em `http://localhost:5001/health` no mapeamento acima. Não depende de Node, pnpm ou banco no runtime. Não execute esse container e a API local simultaneamente na mesma porta 5001.
