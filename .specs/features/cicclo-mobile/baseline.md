# Baseline observado para o planejamento mobile

**Data:** 2026-10-21. **Resultado:** há bootstrap mobile utilizável em outro worktree; este worktree ainda está vazio. API de negócio não foi encontrada na base inspecionada.

## Git e locais

| Local | Branch/HEAD observado | Estado observado |
| --- | --- | --- |
| `/Users/digitalrepublic/orca/workspaces/teste-cicclo/mobile-worktree` | `mobile-worktree`, `84eaf982ceda4c0967a811e02d53bd0993e31f7f` | Apenas `.git`, working tree inicialmente limpa |
| `/Users/digitalrepublic/Documents/projetos-pessoais/teste-cicclo` | `feat/bootstrap-monorepo`, `9788d0b68fe40ee287750bb71f50c47ecb7af056` | Bootstrap commitado; `.gitignore` modificado localmente, preservado |

`main` local observado aponta ao commit inicial. Não foi feita integração, troca de branch, commit, push ou operação remota.

## Evidência de código/configuração existente

Caminhos a seguir são relativos ao worktree de bootstrap.

| Evidência | Descoberta |
| --- | --- |
| `package.json:5–15` | pnpm 10.33.0, Node 22.22.3+, Turbo e comandos de workspace |
| `pnpm-workspace.yaml:1–9` | `apps/*`, `packages/*`, linker isolated; lockfile único |
| `apps/mobile/package.json:5–35` | entry `index.ts`, scripts dev/android/ios/build/lint/typecheck/test, preset jest-expo |
| `apps/mobile/package.json:15–31` | Expo 58.0.16, React 19.3.0, RN 0.88.0, TS 6.0.3, Jest 30.5.2, jest-expo 58.0.11, RNTL 14.1.0 |
| `apps/mobile/App.tsx:1–18` | Única tela de apresentação, sem feature/API |
| `apps/mobile/__tests__/App.test.tsx:5–16` | Um smoke de render que exige Cicclo centralizado e nenhuma requisição; requisito a substituir explicitamente ao migrar experiência |
| `apps/mobile/tsconfig.json:1–8` | `strict: true`, inclui TS/TSX |
| `apps/mobile/eslint.config.js:1–7` | ESLint flat do Expo, ignora build/cache |
| `apps/mobile/app.json:1–10` | Android/iOS, sem Router ou esquema de deep link configurado |
| `apps/api/src/Program.cs:1–8` | Apenas health checks e `GET /health` |
| `tests/bootstrap.test.mjs:18–45` | Lockfile pnpm único, versões mobile exatas e matriz Expo 58/RN/React |
| `tests/bootstrap.test.mjs:55–63` | Gate de bootstrap depende de builds mobile e API; não confundir com teste só mobile |
| `README.md:35–62` | Desenvolvimento API na porta 5001; mobile inicial não consome API |
| `README.md:64–82` | Gates e provas adicionais documentados; não foram todos executados nesta sessão |

Não foram encontrados `AGENTS.md` locais no workspace ou na base inspecionada. Não há `.specs/STATE.md` anterior neste worktree. O comando de lessons retornou `(no confirmed lessons)`.

## Verificações realmente executadas

Os comandos foram executados no worktree existente, não na árvore vazia. Não houve instalação de dependências nem edição da aplicação.

| Comando | Resultado observado |
| --- | --- |
| `git status --porcelain=v1 -b` em ambos os worktrees | Estados registrados acima |
| `git log -5 --oneline --decorate` no worktree mobile | Apenas commit inicial `84eaf98` |
| `git worktree list --porcelain` | Os dois worktrees do mesmo repositório |
| `git ls-tree -r --name-only HEAD` na base | Estrutura commitada `apps/mobile`, `apps/api` e testes de bootstrap |
| `pnpm --filter @cicclo/mobile typecheck` | PASS, exit 0 |
| `pnpm --filter @cicclo/mobile lint` | PASS, exit 0, zero warnings ESLint |
| `pnpm --filter @cicclo/mobile test` | PASS, 1 suíte / 1 teste / 0 snapshots; aviso de recrawl do Watchman |
| `validate_spec.py .specs/features/cicclo-mobile/spec.md --strict` pela skill | PASS estrutural, zero erros e zero avisos |
| Conferência dos IDs da spec | 60 IDs únicos, todos referidos no conteúdo; 50 P0 e 10 P1 |
| `git diff --check` no worktree de planejamento | PASS para alterações rastreadas; arquivos novos revisados separadamente |

O aviso de Watchman não falhou a suíte. Não foram executados comandos de limpeza sugeridos pelo aviso.

O gate estrutural da spec não valida implementação. A revisão humana e os futuros gates funcionais continuam pendentes. O roteiro contém 21 intenções de commit distribuídas pelas sete etapas E0–E6, além de três incrementos P2 opcionais sem execução autorizada.

## Não validado agora

- Export/build mobile, gates da API e workspace completo.
- Execução Android ou iOS; conectividade real com app nativo.
- Operações financeiras, wallet, catálogo, booking, OpenAPI de negócio, filas ou SignalR.
- API rodando em qualquer porta: porta 5001 é configuração documentada, não prova de servidor ativo.
- Outras versões dos handoffs de backend encontradas em Downloads. A referência lida foi `cicclo_backend_handoff.md`; variantes numeradas não foram presumidas canônicas.

## Gates externos para a implementação

1. Reconciliar base de integração com bootstrap vigente e preservar trabalho local do usuário.
2. Obter contrato real de wallet/services/bookings antes de aprovar integração dos incrementos correspondentes.
3. Confirmar códigos reais de erro, shapes de GET, paginação, semântica/retenção de idempotência e consistência de saldo.
4. Validar Android com API real antes de declarar P0 entregue.

Detalhes e decisões propostas estão em [spec.md](spec.md).

## Detalhamento pré-aprovado após a revisão da spec

O usuário autorizou design e tarefas sem novo Plannotator, com pré-aprovação documental. Foram produzidos `design.md` e `tasks.md`: 37 tarefas, 30 de P0 e 7 condicionais de P1, nas mesmas sete etapas e branches da spec. P2 permanece opcional e exige ativação separada.

Verificações executadas sobre os documentos nesta etapa:

- `validate_spec.py --strict`: zero erros e zero avisos.
- `validate_tasks.py --strict`: zero erros e zero avisos.
- Os 60 requisitos possuem mapeamento tarefa/prova, sem ID duplicado, desconhecido ou não mapeado.
- As 37 mensagens de commit planejadas passaram individualmente por `check_commit.py`.
- Campos obrigatórios das 37 tarefas e links locais dos cinco documentos foram conferidos, sem lacunas ou links quebrados.
- Não houve execução de tarefas, nova instalação, criação de branch, commit, edição de aplicação ou Plannotator nesta etapa. Os resultados mobile anteriores acima continuam sendo evidência histórica do bootstrap, não prova das features planejadas.

## Execução T1 (E0): baseline reproduzido

**Data:** 2026-10-21. **Branch:** `chore/mobile-00-baseline`, criada de `feat/bootstrap-monorepo` (`9788d0b`), que descende do commit inicial `84eaf98` de `mobile-worktree`. C0 atendido: ancestralidade confirmada com `git merge-base --is-ancestor`, sem colisão (`.specs/` não existe no bootstrap). O `.gitignore` local do outro worktree não foi tocado; `.omc/` não é versionado.

Comandos executados na raiz, cada um com resultado próprio:

| Comando | Resultado |
| --- | --- |
| `pnpm install --frozen-lockfile` | PASS, lockfile inalterado |
| `pnpm --filter @cicclo/mobile typecheck` | PASS, exit 0 |
| `pnpm --filter @cicclo/mobile lint` | PASS, exit 0 |
| `pnpm --filter @cicclo/mobile test` | PASS, 1 suíte / 1 teste (contagem reconferida, não presumida) |
| `pnpm --filter @cicclo/mobile build` | PASS, `expo export --platform all`: Android 601 módulos e iOS 615 módulos empacotados |
| `validate_spec.py` e `validate_tasks.py` com `--strict` | PASS, zero erros e zero avisos |

**Descoberta para os gates Q/F:** o script `test` é `jest --ci --runInBand --verbose` e repassa argumentos ao Jest **sem** o separador `--`. Uso correto: `pnpm --filter @cicclo/mobile test <caminhos>` ou `... test --findRelatedTests <arquivo>`. Com `--`, o Jest trata o que vem depois como padrão de caminho e encerra com "No tests found" e exit 0 (falso positivo). Confirmado: `test __tests__/App.test.tsx` e `test --findRelatedTests App.tsx` rodam 1 teste.

Gates da API (`dotnet`) e `pnpm test:bootstrap` não foram executados em T1; pertencem ao gate W de T30.
