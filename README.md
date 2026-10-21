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
```

Resposta: HTTP 200, `text/plain`, corpo `Healthy`. É somente liveness do processo, não uma declaração de disponibilidade do PostgreSQL. Não há autenticação. A conexão `ConnectionStrings:Cicclo` é resolvida apenas quando um componente de dados a usa; sem ela, a API continua subindo.

Em `Development`, o documento OpenAPI 3.1 gerado pelos endpoints reais fica em `GET /openapi/v1.json` (por exemplo, `curl http://localhost:5001/openapi/v1.json`). Ele lista somente operações já entregues e não é publicado em outros ambientes.

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

Provas adicionais, pela raiz: `node scripts/smoke.mjs install`, `dev`, `lint`, `failures`, `native`, `docker` ou `postgres` (um argumento por execução). As provas `dev` e `native` exigem portas 5001 e 8081 livres; `native` exige macOS, Swift e um simulador iOS disponível. As provas Docker exigem daemon ativo e acesso aos registries. Usam containers/volumes com nomes exclusivos e removem somente esses recursos de teste. Evidência visual nativa fica em `.artifacts/native-ios.png`, ignorada no Git.

## PostgreSQL local com Compose

O Compose em `apps/api/docker-compose.yml` disponibiliza **apenas o banco**, independente do Dockerfile da API. Prepare uma senha local:

```sh
cp apps/api/.env.example apps/api/.env
```

Edite `apps/api/.env`, substituindo `POSTGRES_PASSWORD`, e depois execute na raiz:

```sh
docker compose -f apps/api/docker-compose.yml --env-file apps/api/.env up -d --wait
docker compose -f apps/api/docker-compose.yml --env-file apps/api/.env logs postgres
docker compose -f apps/api/docker-compose.yml --env-file apps/api/.env exec postgres psql -U cicclo -d cicclo
```

Conexão: `127.0.0.1:5432`, banco e usuário `cicclo`, senha definida no `.env`. A porta fica restrita ao loopback; altere `POSTGRES_PORT` se 5432 já estiver ocupada. Nunca use a senha de exemplo em produção. Arquivos `.env` são ignorados pelo Git.

### Seed e volume

O PostgreSQL inicia vazio. As tabelas vêm das migrations do EF Core e `apps/api/init/seed.sql` contém **somente dados iniciais**: wallet BRL com saldo 50.00, serviços `wash` (Lavagem, 18.90) e `dry` (Secagem, 20.90) e as máquinas `washer-01` e `dryer-01`, todas com identificadores estáveis. O arquivo não é montado em `/docker-entrypoint-initdb.d`: ali ele rodaria antes das migrations e apenas em volume novo.

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

## Imagem Docker da API

Na raiz, use `apps/api` como contexto de build:

```sh
docker build -t cicclo-api:local apps/api
docker run --rm -p 127.0.0.1:5001:8080 cicclo-api:local
```

O Dockerfile usa build multi-stage com SDK .NET fixado e runtime ASP.NET Core não-root. A imagem expõe a porta 8080 e responde em `http://localhost:5001/health` no mapeamento acima. Não depende de Node, pnpm ou banco no runtime. Não execute esse container e a API local simultaneamente na mesma porta 5001.
