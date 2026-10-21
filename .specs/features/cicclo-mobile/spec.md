# Cicclo mobile: spec e plano incremental

**Status:** Approved, aprovado pelo usuário no Plannotator em 2026-10-21. Implementação não iniciada.
**Data:** 2026-10-21.
**Escopo:** somente frontend React Native/Expo em `apps/mobile`.
**Fonte funcional:** `cicclo_frontend_handoff.md`, seções 0–22, recebido em `/Users/digitalrepublic/Downloads/`.
**Contrato esperado:** `cicclo_backend_handoff.md`, seção 6. Contrato efetivo: OpenAPI gerado pela API implementada, ainda indisponível na base inspecionada.

## Resumo para revisão

Evoluir o mobile existente, sem recriar o monorepo. Cada incremento tem uma branch exclusiva, uma demonstração verificável e vários commits pequenos, cada um com uma única intenção e seus testes. A ordem é: **base → leitura real → seleção → compra segura → acompanhamento → P0 validado → P1 condicional**.

DRY, KISS, YAGNI e SOLID são regras obrigatórias em todo incremento, não uma limpeza posterior. A arquitetura grita `catalog`, `wallet`, `checkout` e `bookings`. Rotas compõem; features concentram comportamento; infraestrutura compartilhada permanece pequena.

Este arquivo contém os requisitos e o roteiro macro de commits aprovados. O [design detalhado](design.md) e as [37 tarefas executáveis](tasks.md) foram pré-aprovados expressamente pelo usuário nesta conversa em 2026-10-21, sem nova rodada Plannotator. Nenhum incremento foi implementado e nenhuma branch de implementação foi criada nesta sessão.

## Problem Statement

O desafio exige selecionar e contratar serviços de lavanderia com uma carteira demo, sem confundir seleção local com débito confirmado. Os principais riscos são dupla intenção após timeout, dados financeiros fictícios, contratos presumidos e UI que afirma transições não confirmadas pelo servidor.

O worktree atual está no commit inicial vazio, mas há um bootstrap funcional em outro worktree do mesmo repositório. A primeira entrega deve reconciliar essa base antes de acrescentar funcionalidade. A API ali inspecionada só possui `/health`; portanto, integração de negócio é uma dependência externa explícita, não trabalho de backend embutido neste plano.

## Goals

- [ ] Entregar P0 demonstrável contra API real: Home seleciona, Checkout contrata e Serviços acompanha.
- [ ] Manter carteira autoritativa, cálculos em centavos e uma intenção financeira por confirmação.
- [ ] Evoluir em branches pequenas, sem regressão dos incrementos integrados.
- [ ] Manter testes de comportamento no mesmo commit do comportamento implementado.
- [ ] Validar Android, acessibilidade e documentação antes de declarar P0 entregue.
- [ ] Implementar P1 apenas com suporte comprovado; não deixar opcionais comprometerem P0.

## Out of Scope

| Exclusão | Motivo |
| --- | --- |
| Alterar backend, banco, migrations, endpoints ou simulador | Mobile é consumidor; gaps são registrados, não corrigidos aqui |
| Login, cadastro, IAM, entidade de usuário ou seleção de wallet | Demonstração usa wallet resolvida pelo servidor |
| Pagamentos reais, controle físico de máquinas e painel operador | Domínio simulado pelo backend |
| Redux/Zustand, Storybook, E2E automatizado, microfrontends | Cortes deliberados do handoff |
| Clean Architecture cerimonial, GenericRepository, command bus, interface por arquivo | Contrariam simplicidade e arquitetura por domínio |
| Recriar/expandir monorepo, CI/CD, deploy e publicação em loja | A base já existe; não são necessários para a experiência solicitada |
| Offline-first e fila automática de comandos financeiros | Risco de duplicação e escopo desnecessário |
| SignalR, persistência e APK como condição de P0 | P2 opcional, sujeito a decisão posterior e tempo disponível |

## Assumptions & Open Questions

Os defaults abaixo foram aprovados na revisão global do Plannotator em 2026-10-21 e detalhados sob pré-aprovação expressa nesta conversa. A aprovação não afirma que dependências externas já existem nem resolve os bloqueios factuais registrados.

| Assumption / decision | Chosen default | Rationale | Confirmed? |
| --- | --- | --- | --- |
| Base do mobile | Reutilizar o bootstrap commitado de `feat/bootstrap-monorepo`, inspecionado em `9788d0b`; reconciliar a versão vigente em E0 | Evitar scaffold duplicado e preservar pnpm/Expo existentes | Confirmado pelo usuário no Plannotator em 2026-10-21 |
| Local da aplicação | `apps/mobile`, dentro do monorepo existente | Estrutura real, não a raiz vazia deste worktree | Verificado |
| Branch por etapa | Etapa = incremento E0–E6; cada uma usa nova branch. P2 tem branches próprias se aprovado | Não confundir uma etapa com cada commit | Aprovado |
| Base das branches | Criar cada branch a partir da base de integração que já contenha a anterior aprovada; sem branches empilhadas por padrão | Simplifica revisão, integração e reversão | Aprovado |
| Base de integração | Usar `mobile-worktree` como integração local; criar E0 a partir do bootstrap vigente e integrar etapas somente por fast-forward após gates, conforme tasks.md. Parar se houver divergência; não alterar main ou o outro worktree | Resolve o fluxo sem recriar base ou reescrever histórico; execução ainda não iniciada | Pré-aprovado nesta etapa; ancestralidade é gate factual em E0 |
| Stack | Preservar Expo 58.0.16, RN 0.88.0, React 19.3.0, TS 6.0.3, pnpm 10.33.0 e Jest/RNTL existentes, salvo incompatibilidade comprovada | Sem upgrades cosméticos | Verificado na base; reconferir em E0 |
| Arquitetura | Features por domínio com funções/hooks concretos e componentes de apresentação | Já determinada pelo usuário e pelo handoff | Confirmado pela solicitação |
| Prioridades | Conservar P0/P1/P2 do handoff, sem renumerar MVP como P1 | Evita ambiguidade de escopo | Confirmado pela fonte |
| Prazo de dois dias | Timebox do desafio inteiro; ordem orienta corte, não promete todas as etapas nesse prazo | Backend ainda não oferece operações de negócio na base inspecionada | Fonte; estimativa não prometida |
| API de negócio ausente | Permitir testes e protótipos isolados com fixtures; app executável apresenta indisponibilidade honesta. Não aprovar gate de integração real por mocks | Não fabricar saldo, histórico ou sucesso | Confirmado pela fonte |
| Contratos temporários | Tipos locais mínimos baseados no handoff, marcados temporários; substituir por tipos gerados ao obter OpenAPI real | Desbloqueia trabalho local sem transformar exemplos em contrato verificado | Aprovado |
| Endpoints P0 faltantes | Catálogo/carteira/POST ausentes bloqueiam P0. Listagem/detalhe ausentes aceitam UI honesta de limitação, mas não DoD de P0 completo | Handoff permite fallback, porém DoD final exige acompanhamento real | Aprovado |
| P1 | Só E6 e somente ações comprovadas no OpenAPI e no ambiente; suportes ausentes ficam ocultos | Não presumir endpoints nem usar 404 como detecção recorrente | Confirmado pela fonte |
| Persistência da intenção | P0 retém intenção na sessão, incluindo troca de abas e background sem encerrar processo. Recuperação após encerramento só em P2a | Mantém corte explícito do handoff; não promete garantia entre reinícios | Aprovado; risco conhecido |
| Retenção de idempotência | Não presumir TTL infinito. Verificar escopo/retenção/replay antes do gate de compra real | Repetir além da retenção pode ter efeito diferente | Bloqueio contratual em E3 |
| Reconexão | Refetch GET automático; reconciliação financeira apenas pelo CTA explícito com a intenção original | Recupera estado sem retry financeiro invisível | Aprovado, alinhado à seção 12.3 |
| Cache monetário | Sem decremento otimista. GET em voo é cancelado antes de aplicar resposta confirmada; refetch posterior respeita consistência comprovada | Evita resposta antiga sobrescrever confirmação mais nova | Confirmado pela fonte |
| Visual | Tema demo único e legível, sem inventar identidade oficial | Não bloquear produto por design extra | Aprovado |
| Smoke existente | A aprovação da migração E1 autoriza substituir o contrato obsoleto de tela inicial centrada/sem API por testes de rotas/leitura; preservar ou ampliar sua cobertura útil | O teste atual conflita explicitamente com a nova experiência. Não remover apenas para obter verde | Aprovado na spec |
| Operações Git/remotas | Agora apenas arquivos de planejamento. Execute futuro permite commits locais após aprovação; push, merge remoto, PR, deploy e destrutivas exigem autorização específica | Escopo solicitado é planejamento e revisão | Confirmado pela solicitação e skill |

**Open questions: none** fora das premissas e bloqueios explicitamente registrados. Falta de OpenAPI, integração da base e comportamento de replay são gates factuais antes da execução correspondente; não são resolvidos por aprovação documental.

## Reconhecimento e matriz de aderência

Evidência detalhada e comandos executados: [baseline.md](baseline.md). Caminhos da base abaixo são relativos ao outro worktree, `/Users/digitalrepublic/Documents/projetos-pessoais/teste-cicclo`.

| Exigência | Existe | Lacuna | Alteração mínima |
| --- | --- | --- | --- |
| Aplicação existente | `apps/mobile/App.tsx` mostra Cicclo; build Expo configurado | Worktree atual não contém bootstrap | Integrar base commitada em E0; não copiar worktree sujo |
| Toolchain e qualidade | pnpm, lock único, TS strict, ESLint, Jest 30, jest-expo 58, RNTL 14 | Só 1 teste estático | Reutilizar comandos/configurações, ampliar testes por comportamento |
| Navegação e UI | Nenhum Router, header, tema ou provider de domínio | 3 abas e detalhe ausentes | Adicionar Expo Router compatível e composição mínima |
| Estado remoto | Nenhum cliente HTTP ou Query instalado diretamente | Catálogo/carteira/bookings | Um cliente fetch + QueryClient global; APIs por feature |
| Contratos | Backend `Program.cs:1–8` só `/health`; handoffs disponíveis | Sem OpenAPI de negócio | Gate de contrato; tipos temporários mínimos fora de `generated/` |
| Seleção | Não existe | Quantidade, centavos, preferência, pré-reserva | Context+reducer só para seleção |
| Compra | Não existe | Intenção, idempotência, incerteza, cache | Fluxo financeiro testado antes de habilitar CTA real |
| Acompanhamento | Não existe | Lista, filtros, detalhe, atualização | Consumir GETs reais sem histórico paralelo em Context |
| Filas e ações | Não verificadas no backend | Capacidades P1 | Implementar somente após gate P0 e contrato P1 |
| Documentação | README raiz do bootstrap | Instruções do produto mobile | README em `apps/mobile` e link pequeno na raiz; preservar onboarding da API |

## User Stories

Os identificadores em cada critério são a fonte da rastreabilidade. Palavras EARS (`WHEN`, `IF`, `WHILE`, `WHERE`, `SHALL`) são mantidas para validação; o restante usa português.

### P0: navegar e consultar a carteira e o catálogo

Como visitante, quero ver serviços reais e saldo confirmado antes de selecionar uma contratação.

**Acceptance Criteria**:
1. The mobile SHALL manter a ordem visual das abas Serviços, Home e Checkout. [NAV-01]
2. WHEN o app abre sem deep link THEN o mobile SHALL iniciar na Home, mesmo sendo a aba central. [NAV-02]
3. The header SHALL apresentar `Olá! {displayName}` à esquerda, usando `EXPO_PUBLIC_DEMO_DISPLAY_NAME` não vazio ou `Visitante` como fallback. [NAV-03]
4. The header SHALL apresentar à direita `Saldo da carteira` com `balanceCents` confirmado pela API em BRL. [READ-01]
5. WHILE a primeira consulta da carteira está pendente o header SHALL mostrar indicador de carregamento em vez de valor monetário. [READ-02]
6. IF a carteira falha THEN o mobile SHALL mostrar saldo indisponível com ação acessível de retry, sem converter falha em zero. [READ-03]
7. WHEN o catálogo é recebido THEN a Home SHALL renderizar nomes, preços e elegibilidade dos serviços da resposta validada, sem catálogo hardcoded. [READ-04]
8. IF a consulta do catálogo falha ou retorna coleção vazia THEN a Home SHALL apresentar respectivamente erro com retry ou estado de catálogo vazio. [READ-05]
9. IF `isActive=false` ou `canBeBooked=false` THEN a Home SHALL bloquear a adição com motivo textual de indisponibilidade. [READ-06]

**Independent Test:** API fake nos limites retorna wallet 5000 e catálogo 1890/2090; teste de Router comprova aba inicial, ordem das tabs, saudação, loading, erro e leitura. A demonstração usa API real.

### P0: selecionar sem debitar

Como visitante, quero revisar várias execuções e saber quanto ainda posso selecionar sem alterar meu saldo real.

**Acceptance Criteria**:
1. WHEN a carteira é 5000 e a seleção contém lavagem 1890 mais secagem 2090 THEN o mobile SHALL calcular selecionado 3980 e disponível para adicionar 1020, mantendo o header em 5000. [SEL-01]
2. WHEN adicionar uma unidade excederia o saldo disponível THEN o mobile SHALL manter a seleção anterior com mensagem acessível de saldo insuficiente. [SEL-02]
3. WHILE carteira ou catálogo estão indisponíveis, inválidos ou sem confirmação após reconexão o mobile SHALL bloquear novas adições e contratação. [SEL-03]
4. WHEN a seleção já contém 10 ocorrências THEN o mobile SHALL impedir a 11ª ocorrência com explicação do limite. [SEL-04]
5. WHEN o mesmo serviço é adicionado duas vezes com saldo suficiente THEN o checkout SHALL preservar quantidade 2 e expandi-la para duas entradas em `serviceCodes`. [SEL-05]
6. WHEN o usuário troca entre as três abas THEN o checkout SHALL preservar a seleção em memória no provider acima das abas. [SEL-06]
7. WHEN a última ocorrência do serviço preferido é removida THEN o checkout SHALL limpar a preferência desse serviço. [SEL-07]
8. WHEN preço ou elegibilidade muda após a seleção THEN o checkout SHALL bloquear o consentimento antigo até nova revisão explícita da seleção válida. [SEL-08]
9. WHILE o checkout está vazio o mobile SHALL oferecer `Escolher serviços` para a Home sem permitir POST. [SEL-09]
10. The checkout SHALL apresentar subtotal, saldo confirmado e saldo estimado após contratação com rótulos distintos. [SEL-10]
11. The badge do Checkout SHALL mostrar a soma das quantidades, ocultando zero e incluindo quantidade no nome acessível da aba. [SEL-11]

**Independent Test:** reducer/seletores puros e interação com providers reais; testar 0, 1, 10, 11, duplicatas, remoção e seleção preservada.

### P0: contratar com intenção única e recuperação explícita

Como visitante, quero uma confirmação financeira precisa e recuperar uma resposta perdida sem criar outra compra.

**Acceptance Criteria**:
1. WHEN confirmar uma seleção válida de 1–10 itens THEN o mobile SHALL enviar `POST /api/v1/bookings` com `serviceCodes`, `expectedTotalCents`, preferência opcional e UUID v4 em `Idempotency-Key`. [BUY-01]
2. WHILE uma intenção está `submitting` ou `unknown` o mobile SHALL impedir a criação de outra intenção de compra. [BUY-02]
3. WHEN dois toques ocorrem antes do próximo render THEN o checkout SHALL disparar no máximo um envio inicial, protegido também no comando e não apenas por botão disabled. [BUY-03]
4. WHEN o POST retorna 201 ou replay 200 válido THEN o mobile SHALL tratar o booking retornado como contratação confirmada única. [BUY-04]
5. WHEN a compra é confirmada THEN o header SHALL apresentar `remainingBalanceCents` retornado, sem permitir que GET iniciado antes da confirmação sobrescreva esse valor. [BUY-05]
6. WHEN a compra é confirmada THEN o checkout SHALL limpar a seleção e oferecer acesso à contratação confirmada. [BUY-06]
7. WHEN a compra é confirmada THEN o mobile SHALL invalidar seletivamente wallet, listagem e detalhe pertinente para recuperar estado autoritativo. [BUY-07]
8. IF ocorre timeout, perda de rede, 5xx ou resposta de sucesso inválida após envio THEN o checkout SHALL conservar chave e payload congelados em estado `unknown`, sem declarar falha financeira ou sucesso. [BUY-08]
9. WHEN o usuário aciona `Verificar contratação` em uma intenção recuperável THEN o mobile SHALL reenviar a mesma chave e o mesmo payload original, independentemente de alterações posteriores do catálogo. [BUY-09]
10. IF a API retorna `409 PRICE_CHANGED` definitivo THEN o checkout SHALL exigir novo consentimento após refetch de catálogo e carteira, usando nova chave somente na nova intenção aprovada. [BUY-10]
11. IF a API recusa por saldo insuficiente com o código comprovado no contrato THEN o mobile SHALL atualizar carteira e explicar a recusa sem apresentar cobrança confirmada. [BUY-11]
12. IF a API retorna 429 THEN o comando SHALL impedir nova tentativa antes do prazo válido de `Retry-After`, sem loop automático de POST. [BUY-12]
13. IF a API retorna `IDEMPOTENCY_CONFLICT` THEN o mobile SHALL manter a intenção bloqueada para recuperação, sem trocar silenciosamente a chave. [BUY-13]
14. The checkout SHALL informar antes da confirmação que a cobrança é imediata mesmo em fila e que máquinas são simuladas. [BUY-14]

**Independent Test:** integração Home→Checkout→API fake→Header, com fetch diferido e cache real; replay mantém bookingId e usa saldo atual da resposta. Comparar chave e corpo completos nos reenvios. Demo contra API real comprova 5000→3110 para uma lavagem em cenário controlado.

### P0: acompanhar contratações existentes

Como visitante, quero diferenciar serviços ativos do histórico e consultar o que realmente aconteceu.

**Acceptance Criteria**:
1. WHEN a listagem está disponível THEN Serviços SHALL listar bookings reais por `createdAt` decrescente com identidade estável e navegação para detalhe. [BOOK-01]
2. WHEN o filtro é `Em andamento` THEN Serviços SHALL incluir bookings com pelo menos um item não terminal, preservando no resumo os itens terminais do mesmo lote. [BOOK-02]
3. WHEN o filtro é `Histórico` THEN Serviços SHALL incluir apenas bookings cujos itens são todos `completed`, `cancelled` ou `failed`, salvo status agregado diferente confirmado no contrato e registrado antes de implementar. [BOOK-03]
4. IF não há registros no filtro THEN Serviços SHALL mostrar empty state específico com CTA à Home. [BOOK-04]
5. WHEN um booking válido é aberto THEN o detalhe SHALL apresentar preço histórico e estado por item vindos da API, sem recalcular pelo catálogo atual. [BOOK-05]
6. IF o ID de detalhe é inválido ou o booking não existe THEN o mobile SHALL mostrar erro recuperável com retorno para Serviços, sem consulta com ID malformado. [BOOK-06]
7. IF listagem, detalhe ou ação opcional não existe no backend THEN o mobile SHALL expor limitação honesta ou ocultar a ação, sem persistência fake de histórico. [BOOK-07]

**Independent Test:** lote misto continua em andamento; lote só terminal vai ao histórico; fixtures com datas fora de ordem; detalhe 404, ID inválido e falha de rede. Smoke usa bookings persistidos pela API.

### P0: fronteiras confiáveis e experiência nativa

Como avaliador, quero executar e verificar um app simples, acessível e previsível em falhas.

**Acceptance Criteria**:
1. The mobile SHALL validar respostas financeiras e DTOs críticos na fronteira com Zod, rejeitando campos obrigatórios ausentes/tipos errados sem criar valores de fallback. [SAFE-01]
2. The cliente HTTP SHALL classificar erros por status e `errorCode`, incluindo corpo vazio, HTML e JSON inválido, sem lógica baseada em `detail`. [SAFE-02]
3. WHILE o app está offline o mobile SHALL impedir compra com saldo antigo e identificar qualquer valor retido como última atualização. [SAFE-03]
4. WHEN o app volta ao foreground ou reconecta THEN o mobile SHALL refazer consultas relevantes por REST sem disparar POST financeiro automático. [SAFE-04]
5. The mobile SHALL remover listeners/timers de seu escopo ao desmontar, sem multiplicar assinaturas em re-renderizações. [SAFE-05]
6. The ações críticas SHALL possuir role, label e estado acessível coerentes com seu estado visual e feedback textual que não dependa só de cor. [UX-01]
7. WHEN fonte do sistema é ampliada e as três abas são abertas em Android THEN o layout SHALL manter saudação, saldo e ações legíveis, com scroll quando necessário e sem duplicar safe-area insets. [UX-02]
8. The README mobile SHALL documentar instalação, URL de API por dispositivo, fluxo, pré-reserva, idempotência, comandos, resultados reais e limitações implementadas. [QUAL-01]
9. The entrega P0 SHALL ter lint, typecheck, testes, export mobile e smoke Android aprovados com evidência de execução e sem testes ignorados para passar o gate. [QUAL-02]

**Independent Test:** Jest/RNTL de fronteira, lifecycle e a11y; smoke manual Android com API real, fonte ampliada, suspensão/retomada e rede indisponível. Export não substitui execução nativa.

### P1: operar ofertas, fila e incidentes, condicionado ao backend

Como visitante, quero usar e acompanhar serviços pagos sem o app inventar disponibilidade, transições ou estornos.

**Acceptance Criteria**:
1. WHERE endpoints P1 estão comprovados o detalhe SHALL oferecer ativação apenas para `pending_activation` e confirmação apenas para `to_be_confirmed` ainda não expirado visualmente. [QUEUE-01]
2. WHEN um item está `queued` THEN o mobile SHALL mostrar posição autoritativa quando presente e explicar que o serviço já está pago. [QUEUE-02]
3. WHEN uma oferta tem `expiresAt` THEN o contador SHALL renderizar `max(0, ceil((expiresAt - agora)/1000))`, sem assumir execução de timer durante background. [QUEUE-03]
4. WHEN o contador chega a zero THEN o mobile SHALL desabilitar confirmação visual e consultar o servidor, sem transicionar localmente o item para fila. [QUEUE-04]
5. IF confirmação retorna 410 ou ação retorna 409 de transição THEN o mobile SHALL atualizar o detalhe e comunicar a divergência com base no novo estado. [QUEUE-05]
6. WHERE cancelamento é suportado o mobile SHALL oferecê-lo somente em `pending_activation`, `queued` ou `to_be_confirmed`, sem cancelamento voluntário em `in_progress`. [QUEUE-06]
7. WHEN cancelamento ou falha possui estorno confirmado pela API THEN o mobile SHALL apresentar esse estorno e atualizar carteira, sem creditá-la antecipadamente. [QUEUE-07]
8. WHERE relato de problema está implementado e é aceito o mobile SHALL mostrar `Relato enviado para verificação`, sem afirmar estorno. [QUEUE-08]
9. WHILE a tela relevante está visível, app ativo, online e há itens não terminais o mobile SHALL realizar polling REST seletivo, suspendendo-o fora dessas condições. [QUEUE-09]
10. WHEN existe oferta na aba Serviços THEN a lista SHALL destacar a oferta e seu prazo sem exigir que o usuário adivinhe abrir um detalhe. [QUEUE-10]

**Independent Test:** fake clock para 60, 1 e 0 segundos, foreground após expiração, 409/410, estorno ausente/presente, report 202 e ausência de cancelamento após início. Confirmar validade continua sendo decisão do servidor.

### P2: opcionais fora do compromisso de entrega

Se aprovados posteriormente, terão critérios próprios antes de executar:

- **P2a, intenção persistida:** gravar chave+payload antes do POST; validar restauração com Zod; bloquear nova compra enquanto pendente; erro/corrupção não dispara POST; cleanup só após confirmação; não persistir carteira como verdade. Registros sem retenção de replay comprovada não são reenviados automaticamente.
- **P2b, SignalR:** somente Hub/eventos comprovados; notificação apenas invalida query; REST e lifecycle continuam suficientes; cleanup e reconnect testados.
- **P2c, APK:** somente com configuração já viável, autorização para ações externas necessárias e instalação Android testada com URL acessível. Bundle exportado não é APK.

Não instalar dependências opcionais preventivamente. P2 exige decisão separada de execução, não entra silenciosamente ao finalizar P0.

## Edge Cases e decisões de falha

| Situação | Comportamento decidido | Critérios |
| --- | --- | --- |
| Seleção contém serviço removido | Invalidar seleção para compra, permitir remover e explicar; não enviar código inexistente | SEL-08, SAFE-01 |
| Preço sobe e total excede saldo | Exibir novo total e insuficiência; nova revisão não ignora restrição de saldo | SEL-02, SEL-08, BUY-10 |
| Carteira carregando/erro | Sem adição/POST; retry explícito, nunca saldo fictício | READ-02/03, SEL-03 |
| Quantidade 0, negativa, fracionária ou código desconhecido em entrada local | Seleção inválida, sem POST; reducer/seletores não aceitam aritmética inválida | SEL-04/05, BUY-01 |
| Inteiro fora da faixa segura de JavaScript | Recusar dado/total financeiro inválido na fronteira, nunca arredondar silenciosamente | SAFE-01 |
| Timeout/AbortSignal do POST | Abort cancela espera, não prova rollback. Estado unknown; mesma intenção | BUY-08/09 |
| JSON inválido em 2xx do POST | Não dizer rejeitado: cobrança pode existir. Preservar intenção e informar erro de contrato | BUY-08, SAFE-01 |
| Código 4xx desconhecido ou `ProblemDetails` inválido após POST | Não assumir garantia de ausência de débito; manter intenção recuperável até classificar pelo contrato | BUY-08/13, SAFE-02 |
| `Retry-After` segundos ou HTTP-date válido | Bloquear tentativa até prazo; se ausente/inválido, feedback e retry manual, nunca loop automático | BUY-12 |
| Catálogo muda durante unknown | Congelar payload original para replay; não criar nova intenção pela edição da seleção | BUY-02/09 |
| Refetch anterior termina após POST | Cancelar/desconsiderar resposta antiga; somente resposta autoritativa válida atualiza saldo | BUY-05 |
| GET posterior vem de cache backend eventualmente consistente | Não prometer ordenação que API não oferece; registrar comportamento observado e gatear aceitação da política de consistência | BUY-05, gate E3 |
| Reinício do processo no P0 | Não prometer restauração da chave. Documentar limitação; consultar Serviços antes de nova decisão. Recuperação garantida entre reinícios exige P2a | Premissa de persistência |
| Chave mais antiga que retenção comprovada | Não reenviar como compra nova; bloquear recuperação automática e orientar reconciliação pelo servidor/listagem disponível | Gate E3, BUY-09 |
| Lista paginada | Respeitar paginação real; filtros locais só sobre registros carregados, sem anunciar histórico completo indevidamente | BOOK-01/02/03 |
| Status novo não reconhecido | Mensagem de estado não reconhecido/erro contratual recuperável, sem habilitar comandos ou assumir terminal | SAFE-01, BOOK-05 |
| Histórico tem preço diferente do catálogo | Mostrar preço contratado, não preço atual | BOOK-05 |
| Relógio local adiantado/atrasado | Contador é informativo; 410 prevalece mesmo com segundos restantes | QUEUE-03/05 |
| App retorna depois de 60s | Recalcular pelo timestamp e refetch, não retomar contador antigo | SAFE-04, QUEUE-03/04 |

## Varredura das nove dimensões implícitas

| Dimensão | Aterrissagem no escopo |
| --- | --- |
| Validação e limites | BUY-01, SEL-04/05, SAFE-01, BOOK-06: 1–10 ocorrências, UUID, centavos inteiros seguros, preferência no lote |
| Falha e falha parcial | READ-03/05, BUY-08, SAFE-01/02; frontend nunca simula rollback financeiro |
| Idempotência/retry | BUY-02/03/08/09/12/13; payload congelado, replay só por ação explícita |
| Autorização e rate limit | Sem IAM no escopo; wallet resolvida pela API. Sem walletId/userId arbitrários. BUY-12 para 429; nunca chamar `/dev/*` |
| Concorrência/ordenação | BUY-03/05, BOOK-01; dupla interação, GET obsoleto, ordem de histórico e corridas 409/410 |
| Ciclo de vida/expiração | SAFE-04/05, QUEUE-03/04/09; retenção idempotente é gate contratual. Persistência só P2a |
| Observabilidade | SAFE-02: logs sanitizados de kind/status/errorCode/traceId quando disponível; sem body financeiro ou mensagem interna. N/A para plataforma de telemetria mobile nova, não exigida |
| Dependência externa | Falha da API não vira dado fake; OpenAPI/Android são gates explícitos. N/A para circuit breaker, acrescentaria infraestrutura sem necessidade |
| Integridade de transições | BUY-02/04/08/10/13, QUEUE-01/04/05/06/07/08; servidor confirma cobrança, fila, execução e refund |

## Arquitetura obrigatória durante todos os incrementos

### Fronteiras e responsabilidades

```text
apps/mobile/src/
  app/                 rotas e composição Expo Router
  features/
    home/              experiência de seleção
    catalog/           catálogo, query e card de serviço
    wallet/            consulta e apresentação do saldo confirmado
    checkout/          seleção, revisão e intenção de compra
    bookings/          lista, detalhe, status e comandos P1
  shared/
    api/               HTTP, erros comuns e tipos externos gerados
    config/            URL pública e nome demo
    layout/            composição compartilhada do header
    ui/                componentes visuais realmente reutilizados
    theme/             tokens semânticos mínimos
    lib/               apenas utilitários puros transversais já necessários
    testing/           helpers e fakes exclusivos de teste
```

A árvore é destino lógico, não ordem de geração de diretórios. Criar cada pasta somente com seu primeiro consumidor. Sem feature `machines` até haver interface que a justifique. Atomic Design orienta composição; não obriga pastas atoms/molecules/organisms para dois componentes.

- `app/` importa APIs públicas de features e layout; não faz fetch, aritmética financeira nem transições de negócio.
- `shared/` não importa `features/`. O header compartilhado recebe saudação/saldo/ações por props; a composição no layout conecta wallet e checkout.
- Feature consome outra apenas por exports explícitos e pequenos. Home consome catálogo/seleção; checkout pode consumir leitura pública de carteira/catálogo; bookings não conhece internos do checkout. Sem barrel gigante, ciclo ou importação profunda entre features.
- HTTP comum resolve transporte e erros. Cada feature possui seus DTO schemas, funções API e query keys, sem um arquivo global acumulando todos os schemas.
- TanStack Query guarda estado remoto. Context+reducer guarda quantidades e preferência. Hook/coordenador do checkout guarda intenção em sessão acima das tabs. `useState` guarda filtro e estado visual. Nenhuma segunda carteira ou lista de bookings em Context.
- Um `QueryClientProvider` global; um `CheckoutProvider` acima das tabs. Nenhum client/provider por tela.
- Base URL pública é origem configurada; `/api/v1` centralizado uma vez. URL inválida recebe erro de configuração, sem fallback oculto para localhost.
- OpenAPI real gera tipos somente quando acessível. DTOs temporários não entram em `generated/`; não criar cliente gerado adicional quando fetch basta.
- Instalar nativas com resolução do Expo SDK existente. Manter um lockfile pnpm e versões exatas conforme teste de bootstrap; não copiar npm/yarn do handoff.

### Regras de revisão: DRY, KISS, YAGNI e SOLID

| Regra | Evidência exigida em cada commit | Reprovar quando |
| --- | --- | --- |
| DRY | Cálculo de centavos, política de status, erro HTTP e intenção possuem uma implementação autoritativa por responsabilidade | Tela duplica total, status ou política de retry; Query e Context duplicam saldo |
| KISS | Função/hook concreto resolve necessidade atual; alteração explica por que extração é necessária | Há wrapper pass-through, factory, camada ou config sem comportamento próprio |
| YAGNI | Nova pasta, pacote ou abstração tem consumidor no incremento atual e requisito rastreado | Dependência P2 instalada no P0; hook/porta só para extensão hipotética |
| **S: responsabilidade única** | Rota compõe; screen coordena; componente apresenta; selector calcula; API transporta/valida; coordenador gerencia intenção | Screen contém fetch, cálculo financeiro e máquina de submissão; reducer tem I/O |
| **O: aberto/fechado** | Variar apresentação por composição/props e concentrar políticas puras de status/erro em módulos coesos | Novo status exige editar condicionais duplicadas em várias telas ou criar framework de plugins preventivo |
| **L: substituição** | Fake de fetch/tempo preserva contrato real: async, status, rejeição, cancelamento e formato; teste do consumidor vale para a substituição | Fake sempre resolve sucesso, omite ProblemDetails ou faz operação síncrona que mascara corrida |
| I: segregação | Props e exports expõem apenas o necessário ao consumidor atual | Componente visual recebe client ou DTO de booking inteiro sem necessidade |
| D: inversão pragmática | I/O fica na fronteira; cálculos/reducer não dependem de rede; substituição por função pequena só onde necessária | Regra pura importa fetch/storage/router; surge interface por arquivo sem razão |

Sem herança planejada: L também se aplica a contratos de funções e substitutos de teste. O não significa criar extensões hipotéticas. DRY não significa mover código específico para shared antes de haver reutilização real.

### Estados de submissão e autoridade

```text
idle -> submitting -> confirmed
                  -> rejected (somente recusa definitiva comprovada)
                  -> unknown -> submitting (verificar: mesma chave + corpo)
rejected PRICE_CHANGED -> revisão requerida -> nova intenção após consentimento
unknown -> nunca nova chave automática
```

`confirmed` exige resposta autoritativa validada. Falha de parse após POST é resultado financeiro indeterminado. Mudança visual de fila, saldo ou estorno nunca substitui resposta do servidor.

## Plano de desenvolvimento incremental: uma branch por etapa

**Ordem:** E0 → E1 → E2 → E3 → E4 → E5 → gate P0 → E6 condicional. P2 só por decisão posterior. Não executar duas etapas concorrentes na mesma árvore.

**Importante:** as 21 intenções abaixo são o roteiro macro. Foram refinadas nas 37 tarefas atômicas pré-aprovadas em [tasks.md](tasks.md), cada uma com dependência, testes, gate e commit próprios. Esse detalhamento é a fonte de execução; o escopo continua o mesmo. Não dividir commits por extensão de arquivo nem separar teste de implementação.

### E0: reconciliar a base e estabelecer gates

**Branch nova:** `chore/mobile-00-baseline`.
**Entrada:** aprovação desta spec; conferir status/branches/commits atuais nos dois worktrees sem modificar o outro.
**Entrega:** este worktree contém a base mobile existente, o planejamento revisado e baseline reproduzível. Sem feature de negócio.

| Commit proposto | Intenção única | Prova/gate |
| --- | --- | --- |
| `docs(mobile): establish incremental delivery baseline` | Versionar plano aprovado e diagnóstico da base; referenciar bootstrap por SHA | Spec validada, diff só de planejamento e prova de ancestralidade da base |

Integrar bootstrap commitado sem copiar arquivos locais. Se ele já estiver na base de integração, nada a reaplicar. Se ainda estiver só na branch de bootstrap, acordar sua integração antes de features; não fazer merge remoto ou cherry-pick indiscriminado. Conservar o histórico original em vez de reempacotar bootstrap em novo commit.

Rodar `pnpm install --frozen-lockfile` somente durante Execute, conforme necessidade, e gates mobile existentes. Capturar contagem dos testes e conferir `tests/bootstrap.test.mjs`. Catálogo/carteira/booking não são prometidos nesta etapa.

**Não avançar:** conflito Git inexplicado, base não disponível, teste/regressão não diagnosticada ou mudança local alheia.

### E1: Home com catálogo e carteira reais

**Branch nova:** `feat/mobile-01-catalog-wallet`.
**Depende:** E0 integrado. Endpoint real é necessário para aprovação de integração; testes podem ser desenvolvidos com fixtures isoladas.
**Entrega demonstrável:** app abre Home; tabs corretas; header e catálogo mostram API, loading e falhas honestas. Checkout/Serviços têm estados informativos sem ação financeira.

| Commit proposto | Intenção única | Prova/gate |
| --- | --- | --- |
| `feat(mobile): compose domain routes with expo router` | Migrar entrypoint e compor as três abas com Home inicial, safe areas e tema mínimo | Testes de navegação/ordem/saudação; substituir smoke obsoleto só conforme aprovação |
| `feat(api): normalize mobile transport and contract errors` | Cliente fetch único, configuração, ProblemDetails e erro de contrato | Unitários de abort, status, corpo vazio/HTML/JSON inválido, códigos reais e Retry-After |
| `feat(wallet): render confirmed balance with recovery states` | Query wallet, validação Zod e header por props | RNTL de valor real/loading/erro/retry; sem saldo fictício |
| `feat(catalog): render authoritative service availability` | Query e apresentação do catálogo em Home | RNTL ready/empty/error/disabled; preço muda conforme resposta |

Dependências só quando consumidas: Router e pares nativos, safe area, Query e Zod. Se OpenAPI surgir, gerar tipos junto ao primeiro consumo. Registrar versão/hash do documento; não inventar schemas de GET bookings a partir do POST.

**Gate de saída:** lint/typecheck/test/export mobile; API real fornece wallet/services no ambiente-alvo e dados coincidem com a UI. Sem endpoints, etapa fica com integração bloqueada, não entregue como funcional.

### E2: seleção e revisão sem compra

**Branch nova:** `feat/mobile-02-selection-review`.
**Depende:** E1 integrado.
**Entrega demonstrável:** seleção na Home preservada entre abas, pré-reserva correta, Checkout revisa quantidades/preferência e informa que contratação ainda não está habilitada nesta etapa. Nenhum botão aparentemente funcional dispara compra incompleta.

| Commit proposto | Intenção única | Prova/gate |
| --- | --- | --- |
| `feat(checkout): model selection quantities and preference` | Reducer puro e seletores em centavos com limites e limpeza da preferência | Unitários 0/1/10/11, duplicata, remover ausente, preço/código inválido e inteiro seguro |
| `feat(home): select services within confirmed wallet limits` | Provider e ação de seleção validada conectados a catálogo/carteira | RNTL de 5000/3980/1020, erro wallet, indisponibilidade e saldo insuficiente |
| `feat(checkout): review selection across tabs` | Tela de revisão, preferência, badge e empty state | Integração Router preserva seleção; preço alterado exige revisão; nenhuma chamada POST |

**Gate de saída:** gates mobile; demonstração com API real confirma que header não muda ao selecionar. Para testar limite 10 usar saldo de fixture suficiente; não alterar carteira real só para satisfazer esse teste.

### E3: contratação idempotente e recuperação na mesma sessão

**Branch nova:** `feat/mobile-03-safe-booking`.
**Depende:** E2 integrado e gate de contrato financeiro resolvido.
**Entrega demonstrável:** usuário contrata uma ou várias execuções; confirmação, recusa e indeterminação são distintas; duplo toque/retry não criam outra intenção.

| Commit proposto | Intenção única | Prova/gate |
| --- | --- | --- |
| `feat(checkout): submit immutable idempotent booking intents` | API POST tipada + coordenador de intenção e estados; UUID gerado uma vez | Contrato do request, um envio inicial sob corrida, 201/200 e rejeições definitivas |
| `feat(checkout): reconcile uncertain booking outcomes` | Recuperação explícita, retenção da intenção e revisão de preço/rate limit | Mesma chave/corpo após timeout/network/5xx/2xx inválido; nova chave só após novo consentimento |
| `feat(checkout): confirm purchases without stale wallet regressions` | Habilitar CTA financeiro com revisão, cache seguro, limpar seleção e sucesso com CTA a Serviços | Integração Home→Checkout→Header; GET antigo não sobrescreve 1020; invalidations seletivas |

Enquanto E4 não estiver integrada, confirmação pode mostrar ID/itens/saldo na própria tela e CTA à aba Serviços informativa; nunca navegar para detalhe inexistente. Acompanhar a contratação integralmente é a entrega seguinte.

**Gate contratual antes de habilitar compra real:** OpenAPI/implementação e evidência de ambiente comprovam UUID/header/body, duplicatas, limite, preferência, replay 200 com mesmo ID, saldo atual do replay, códigos de rejeição, retenção da chave e consistência de leitura. Se algum ponto não for conhecido, registrar o bloqueio; não obter garantia financeira apenas de mock.

**Gate de saída:** gates mobile e compra real; no cenário controlado com saldo 5000, uma lavagem de 1890 confirma 3110. Replay retorna mesmo booking sem segundo débito. Operações reais de demo consomem saldo; só executá-las na sessão de implementação autorizada e no ambiente demo combinado, sem reset destrutivo.

### E4: Serviços e detalhe autoritativos

**Branch nova:** `feat/mobile-04-booking-tracking`.
**Depende:** E3 integrado; listagem/detalhe reais ou limitação formalmente registrada.
**Entrega demonstrável:** compras podem ser acompanhadas em lista, filtros e detalhe; nenhum controle P1 aparece sem suporte.

| Commit proposto | Intenção única | Prova/gate |
| --- | --- | --- |
| `feat(bookings): list active and historical bookings` | API/query de listagem, classificação de status central e tela Serviços | Lotes mistos, todos terminais, datas, paginação real, empty/error/retry |
| `feat(bookings): display authoritative booking details` | Query/detalhe, rota com UUID validado, preços históricos e retorno à lista | IDs inválidos/404, estados por item e catálogo com outro preço |
| `feat(bookings): recover server state on mobile lifecycle changes` | AppState, um provedor de conectividade e refetch REST; conectar sucesso ao detalhe | Testes de background/reconexão, sem POST automático; listeners únicos/cleanup |

Escolher somente **um** monitor de rede compatível, por padrão `expo-network`, sem NetInfo adicional. AppState e bloqueio offline integram P0 aqui, antecipando apenas a proteção financeira necessária; countdown e polling de fila ficam em E6.

**Gate de saída:** gates mobile; contrato específico de GET list/detail validado; smoke com booking criado em E3. Sem GETs, UI pode ser honesta, mas P0 completo continua bloqueado.

### E5: fechar P0 com qualidade verificável

**Branch nova:** `chore/mobile-05-p0-readiness`.
**Depende:** E4 integrado.
**Entrega demonstrável:** instalação/execução reproduzível, Android validado e matriz de P0 sem lacunas. Não é etapa para acrescentar todos os testes esquecidos: testes nascem nos commits anteriores.

| Commit proposto | Intenção única | Prova/gate |
| --- | --- | --- |
| `fix(mobile): close verified accessibility and native usability gaps` | Apenas correções concretas encontradas em fonte ampliada, safe areas e leitor de tela | Testes de regressão no mesmo commit e smoke Android; se não houver gap, omitir commit vazio |
| `docs(mobile): document verified setup and p0 evidence` | README mobile, link raiz e resultados fiéis, limites de P1/P2 e reinício de intenção | Executar onboarding; registrar dispositivo, API/commit, comandos, resultados e limitações |

**Gate P0:** todos os critérios NAV/READ/SEL/BUY/BOOK/SAFE/UX/QUAL passam; export Android/iOS, lint/typecheck/test; smoke Android real com API; nenhuma resposta fake em produção. Falhas descobertas viram commits corretivos atômicos com testes, não um commit genérico `fix all`.

Após o último commit, Verifier independente confere spec contra resultados e executa sensor de discriminação em scratch isolado. Exemplos de falhas a injetar: gerar chave nova no retry, decrementar saldo na seleção, aceitar total desatualizado e permitir GET antigo sobrescrever confirmação. Testes devem matar essas falhas. O Verifier não muta a árvore real nem usa stash para isolamento.

Relatório em `validation.md` com PASS/FAIL, critérios e evidência `file:line`, comandos, resultados, sensor e faixa Git. O completion gate da skill deve passar antes de declarar P0 entregue. Um PASS estrutural desta spec não é esse PASS funcional.

### E6: fila e ações P1, somente com capacidade real

**Branch nova:** `feat/mobile-06-service-lifecycle`.
**Depende:** gate P0 aprovado e endpoints/DTOs P1 comprovados. A API inspecionada ainda não atende essa entrada.
**Entrega demonstrável:** serviços pagos ativam/confirmam/cancelam conforme regra; fila e prazo são autoritativos; relato não equivale a estorno.

| Commit proposto | Intenção única | Prova/gate |
| --- | --- | --- |
| `feat(bookings): activate paid pending service items` | Comando activate e elegibilidade central | pending_activation, sucesso queued/oferta e corrida 409 |
| `feat(bookings): confirm server-issued machine offers` | Countdown e confirm conectados ao expiresAt; destaque de oferta na lista | 60/1/0s, foreground, 410 e sem transição financeira local |
| `feat(bookings): cancel eligible items with confirmed refund feedback` | Cancel só antes do início e sincronização após estorno autoritativo | Ausência em in_progress, 409 e refund apenas confirmado |
| `feat(bookings): report incidents without implying refunds` | report-issue somente quando suportado e mensagem precisa | 202 aceito sem crédito/estorno; rejeição tratada |
| `feat(bookings): poll visible active service states` | Polling seletivo para lista/detalhe não terminais | Para em background/offline/blur/terminal; retoma sem duplicar observers |

**Gate de saída:** testes de todas as transições habilitadas, smoke real oferta/fila/expiração e Verifier independente de P1. Ações sem suporte são omitidas com limitação documentada; não adicionar feature de máquinas só para preencher arquitetura. Não afirmar FIFO comprovado pelo cliente: o app apenas apresenta ordem/posição recebidas.

### P2: branches opcionais isoladas

| Ordem sugerida | Branch nova | Entrega única | Gate adicional |
| --- | --- | --- | --- |
| P2a | `feat/mobile-07-pending-intent-recovery` | Persistência apenas da intenção, antes do primeiro POST, recuperação após reinício | Processo encerrado depois do commit no servidor e antes do cleanup; replay da mesma intenção, storage corrompido e falha de escrita |
| P2b | `feat/mobile-08-realtime-invalidation` | SignalR real como sinal para refetch | Perda de evento/reconnect ainda converge por REST; sem alteração de saldo por evento |
| P2c | `chore/mobile-09-android-artifact` | APK instalável e testado | Build autorizado, instalação e API acessível fora do localhost do host |

Persistência tem mais valor de segurança que SignalR e microinterações. Nenhuma dessas branches é criada até haver decisão de implementá-la. Seus commits e critérios serão detalhados separadamente.

## Política Git e atomicidade obrigatória

1. Antes de começar uma etapa: reconciliar branch/status/commits, confirmar predecessor integrado e criar a nova branch nomeada. Nunca reutilizar a branch anterior para duas etapas.
2. Base de integração tem de conter os commits aprovados da etapa anterior. Preferir etapas sequenciais integradas a uma cadeia longa de branches empilhadas. Se a integração ainda não ocorreu, parar e combinar próximo passo.
3. Um commit corresponde a uma única intenção verificável. Código, teste correspondente, dependência/lockfile necessários e rastreabilidade pertencem ao mesmo commit. Não fazer `feat` sem testes seguido de `test` para corrigir ausência de cobertura.
4. Marcar tarefa concluída em `tasks.md` e atualizar rastreabilidade antes do commit que contém a implementação. O gate da tarefa precisa passar antes de marcá-la concluída.
5. Cada commit compila e passa seu gate; no fechamento da etapa passa a suíte completa mobile. Sem skip, remoção ou relaxamento de testes para obter verde.
6. Conventional Commits com scope do domínio. Executar `check_commit.py --message` antes de cada commit. Não fazer um único commit por etapa se houver várias intenções independentes.
7. Usar staging de caminhos/hunks revisados. Não usar `git add .` para capturar alterações desconhecidas. Não incluir outro worktree, arquivos de backend, segredos, artefatos de build ou alterações locais alheias.
8. Sem reset, stash de trabalho alheio, force-push ou reescrita dos commits do usuário. Resolução de conflito que mude decisão exige nova revisão.
9. Push, criação de PR, merge remoto, deploy e operação destrutiva não são autorizados por este plano. Pedir autorização para a ação específica.
10. Não usar squash que destrua a trilha atômica ao integrar etapas, salvo mudança explícita dessa política pelo usuário.

## Estratégia de testes e gates

### Matriz orientada ao risco

| Superfície | Teste e oráculo | Momento |
| --- | --- | --- |
| Reducer/seletores/dinheiro | Jest, resultados definidos em SEL; limites e inteiros seguros | Mesmo commit que cria/altera regra |
| HTTP/schema/contrato | Jest em fetch/DTO; parse de sucesso/erro/corpo inválido e compatibilidade com tipos gerados | Primeiro consumo e cada mudança de DTO |
| UI/Router | RNTL 14 e `expo-router/testing-library` compatível; queries por role/label, sem snapshot gigante | Cada tela/ação e migração do Router |
| Compra/cache/concorrência | Integração com QueryClient isolado real e fetch fake diferido; providers reais | Cada mudança financeira, não só no fechamento |
| Lifecycle/tempo/P1 | AppState/rede falsos na fronteira, fake clock, cleanup e polling | Mesmo commit do comportamento |
| Arquitetura | Revisão de dependências: shared→features proibido, ciclos/imports internos indevidos e HTTP nas telas | Cada commit e fechamento da etapa; automatizar só com tooling existente ou regra simples necessária |
| Produto nativo | Smoke manual Android com API real, a11y, fonte ampliada e falhas de rede | Primeira execução nativa viável, obrigatório para E5 e capacidades de E6 |

Não há E2E automatizado. Mocks só nos limites. Não mockar Query inteiro para afirmar correção de cache. Não perseguir 100% global; cada AC e cada borda aplicável deve ter prova, e riscos financeiros recebem testes adversariais.

### Comandos reais reaproveitados

Executar na raiz da base integrada, não no worktree vazio antes de E0:

```sh
pnpm --filter @cicclo/mobile typecheck
pnpm --filter @cicclo/mobile lint
pnpm --filter @cicclo/mobile test
pnpm --filter @cicclo/mobile build
```

Os três primeiros foram executados na base existente e passaram em 2026-10-21. Build/export e Android **não foram executados nesta sessão de planejamento**.

- **Por commit:** testes relevantes + typecheck + lint; configuração de build exige também export. Reexecutar suíte completa ao fechar etapa.
- **Por etapa:** todos os comandos acima, evidência de demonstração correspondente e checklist DRY/KISS/YAGNI/SOLID.
- **Workspace:** executar `pnpm build`, `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm test:bootstrap` no fechamento quando a base/backend vigentes permitirem. Falhas fora do mobile são registradas com origem; não alterar backend para escondê-las.
- **Baseline:** preservar contrato de lockfile único, versões exatas e strict de `tests/bootstrap.test.mjs`. Novas dependências respeitam o teste existente.
- **Ambiente ausente:** falta de API, Android ou ferramenta produz BLOCKED com motivo, nunca PASS por inferência.

### Verificação independente

No futuro Execute, o plano terá mais de um lote de tarefas. Oferecer execução por subagentes antes de implementar, sem iniciá-los sem aceite. Execução inline continua válida. O Verifier final é obrigatório e independente, conforme a skill; não é substituído por autoavaliação do autor.

O [tasks.md](tasks.md) contém `Test Coverage Matrix`, `Gate Check Commands`, dependências, `Tests`, `Gate`, critério de conclusão e commit por tarefa, sob pré-aprovação expressa. Rodar `validate_tasks.py` antes de Execute e após alterar tarefas. Os mínimos de casos planejados não são contagens de execução: registrar contagem real por commit e provas derivadas dos ACs.

## Requirement Traceability

Cada ID corresponde ao AC identificado acima, não apenas a um capítulo genérico. Status de implementação de todos continua Pending: nenhum comportamento foi implementado. A coluna de etapa define o destino; o mapeamento dos 60 IDs para tarefas e provas já está em [tasks.md, Requirement → Task → Proof](tasks.md#requirement--task--proof). Pré-aprovação documental não significa Verified.

| Requirement ID | Comportamento | Etapa | Status |
| --- | --- | --- | --- |
| NAV-01 | Ordem visual das tabs | E1 | Pending |
| NAV-02 | Home inicial | E1 | Pending |
| NAV-03 | Saudação demo | E1 | Pending |
| READ-01 | Saldo confirmado | E1 | Pending |
| READ-02 | Loading sem dinheiro fictício | E1 | Pending |
| READ-03 | Erro/retry carteira | E1 | Pending |
| READ-04 | Catálogo da API | E1 | Pending |
| READ-05 | Catálogo vazio/erro | E1 | Pending |
| READ-06 | Serviço indisponível | E1 | Pending |
| SEL-01 | Centavos e pré-reserva | E2 | Pending |
| SEL-02 | Bloqueio saldo insuficiente | E2 | Pending |
| SEL-03 | Bloqueio sem dados confiáveis | E2/E4 | Pending |
| SEL-04 | Limite 10 | E2 | Pending |
| SEL-05 | Múltiplas unidades | E2 | Pending |
| SEL-06 | Seleção entre tabs | E2 | Pending |
| SEL-07 | Limpeza de preferência | E2 | Pending |
| SEL-08 | Revisão de mudança | E2/E3 | Pending |
| SEL-09 | Checkout vazio | E2 | Pending |
| SEL-10 | Resumo financeiro explícito | E2 | Pending |
| SEL-11 | Badge quantitativo acessível | E2 | Pending |
| BUY-01 | Request exato e UUID | E3 | Pending |
| BUY-02 | Intenção pendente bloqueia nova compra | E3 | Pending |
| BUY-03 | Duplo toque | E3 | Pending |
| BUY-04 | 201/200 confirmados | E3 | Pending |
| BUY-05 | Saldo sem corrida antiga | E3 | Pending |
| BUY-06 | Limpar após confirmação | E3 | Pending |
| BUY-07 | Invalidations seletivas | E3/E4 | Pending |
| BUY-08 | Resultado indeterminado | E3 | Pending |
| BUY-09 | Replay mesma chave/corpo | E3 | Pending |
| BUY-10 | PRICE_CHANGED e consentimento | E3 | Pending |
| BUY-11 | Saldo insuficiente no servidor | E3 | Pending |
| BUY-12 | Rate limit | E3 | Pending |
| BUY-13 | Conflito idempotente | E3 | Pending |
| BUY-14 | Aviso de cobrança imediata/simulação | E3 | Pending |
| BOOK-01 | Lista real ordenada | E4 | Pending |
| BOOK-02 | Filtro em andamento/misto | E4 | Pending |
| BOOK-03 | Filtro terminal | E4 | Pending |
| BOOK-04 | Estados vazios | E4 | Pending |
| BOOK-05 | Detalhe/preço histórico | E4 | Pending |
| BOOK-06 | UUID/404 recuperáveis | E4 | Pending |
| BOOK-07 | Limitação honesta | E1/E4 | Pending |
| SAFE-01 | Validação de fronteira | E1/E3/E4 | Pending |
| SAFE-02 | Erro tipado | E1/E3 | Pending |
| SAFE-03 | Offline não autoriza compra | E4 | Pending |
| SAFE-04 | Foreground/reconexão REST | E4 | Pending |
| SAFE-05 | Cleanup de listeners/timers | E4/E6 | Pending |
| UX-01 | Ações e feedback acessíveis | E1–E6 | Pending |
| UX-02 | Fonte ampliada e safe areas | E1/E5 | Pending |
| QUAL-01 | README fiel | E5 | Pending |
| QUAL-02 | Gates e Android reais | E5 | Pending |
| QUEUE-01 | Ativar/confirmar elegíveis | E6 | Pending |
| QUEUE-02 | Fila paga autoritativa | E6 | Pending |
| QUEUE-03 | Contador por timestamp | E6 | Pending |
| QUEUE-04 | Expiração visual sem transição local | E6 | Pending |
| QUEUE-05 | Corridas 409/410 | E6 | Pending |
| QUEUE-06 | Cancelar só antes de iniciar | E6 | Pending |
| QUEUE-07 | Estorno confirmado | E6 | Pending |
| QUEUE-08 | Relato não é estorno | E6 | Pending |
| QUEUE-09 | Polling seletivo | E6 | Pending |
| QUEUE-10 | Oferta visível em Serviços | E6 | Pending |

**Cobertura planejada:** 60 requisitos, 50 P0 e 10 P1 condicionais, todos alocados em incrementos; nenhum implementado/verificado ainda. P2 terá IDs e testes próprios se aprovado.

### Correspondência com a matriz do handoff

| Casos originais | Requisitos desta spec |
| --- | --- |
| FE-001–005 | NAV-01/02/03, READ-01/02/03/04/05 |
| FE-006–011 | SEL-01/02/03/04/05/06/09, READ-06 |
| FE-012–015 | BUY-01/02/03/04/05/06 |
| FE-016–020 | BUY-08/09/10/11/12/13, SAFE-01/02 |
| FE-021–024 | BOOK-01/02/03/04, BUY-07, UX-01 |
| FE-101–110 | QUEUE-01–09, SAFE-03/04/05 |

## Risks & Concerns

| Risco/evidência | Impacto | Mitigação e gate |
| --- | --- | --- |
| Este worktree em `84eaf98`, bootstrap em `9788d0b` | Scaffold duplicado ou branch base errada | E0 integra/reconcilia histórico; não copiar outro worktree |
| Outro worktree tem `.gitignore` modificado localmente | Perda de trabalho do usuário | Somente leitura; não incluir diff alheio |
| Backend só `/health`, `apps/api/src/Program.cs:1–8` | Endpoints não podem ser presumidos | Gates E1/E3/E4; indisponibilidade honesta não equivale a P0 pronto |
| Frontend §10 usa `INSUFFICIENT_BALANCE`/`SERVICE_UNAVAILABLE`; backend §6.7 usa `WALLET_INSUFFICIENT_BALANCE`/`SERVICE_DISABLED`/`NO_OPERATIONAL_MACHINE` | Recusa pode ser mapeada errado | OpenAPI/implementação real decide; aliases só se realmente suportados |
| GET list/detail sem DTO fechado no handoff | Schema de POST reutilizado indevidamente | Validar schema/paginação separados no contrato real |
| `App.test.tsx:5–16` exige tela estática sem API | Migração legítima conflita com teste antigo | Aprovação explícita da substituição do requisito, sem apagar cobertura para passar |
| Bootstrap exige versões exatas, `tests/bootstrap.test.mjs:38–45` | Instalação com ranges quebra regressão | Resolver compatibilidade Expo, pinagem exata e lock atualizado no mesmo commit |
| P0 em memória perde intenção ao encerrar processo | Não há garantia de recuperação financeira entre reinícios | Declarar limite; P2a antes de SignalR; não vender proteção inexistente |
| Sem Android validado nesta sessão | Bundle pode compilar sem UX funcional no aparelho | Smoke Android é gate E5; se ambiente faltar, entrega bloqueada |
| Prazo compartilhado de dois dias e muitos opcionais | Escopo compromete núcleo | Gate P0 antes de E6; cortar P2 primeiro, sem cortar correção financeira/testes |

## Success Criteria e revisão

**Histórico da revisão em 2026-10-21:** na primeira rodada (`annotated`), o usuário confirmou o reaproveitamento do bootstrap. Na segunda rodada, o Plannotator retornou `approved`, sem novas anotações. A spec e o roteiro incremental estão aprovados; nenhum incremento foi implementado.

- [x] Spec aprovada no Plannotator, inclusive premissas de base, branches e persistência.
- [x] Nenhum arquivo de aplicação alterado antes dessa aprovação.
- [ ] Cada incremento começa em branch nova e termina com commits atômicos/gates rastreáveis.
- [ ] DRY/KISS/YAGNI/SOLID e direção dos imports revisados em cada commit.
- [ ] P0 funciona contra API real com todos os 50 requisitos aplicáveis comprovados.
- [ ] P1 só é anunciado após capacidades reais e 10 requisitos condicionais verificados.
- [ ] Nenhuma validação de mock, export ou documento é apresentada como execução Android/API real.
- [ ] Verifier independente e sensor aprovados antes de declarar entrega concluída.

**Próximo passo:** quando o usuário solicitar implementar, reconciliar Git/STATE, ler implement.md e iniciar o plano executável a partir de T1. Design e tarefas já estão pré-aprovados, sem novo Plannotator. Isso não dispara implementação, push ou PR automaticamente nesta sessão.

## Referências técnicas consultadas

Além dos handoffs e arquivos reais listados em `baseline.md`, foram consultadas via Context7 em 2026-10-21:

- [Expo Router: instalação](https://docs.expo.dev/router/installation/), [Tabs](https://docs.expo.dev/router/advanced/tabs/) e [testes](https://docs.expo.dev/router/reference/testing/): migração de entrypoint, root layout e compatibilidade RNTL.
- [Expo: testes unitários](https://docs.expo.dev/develop/unit-testing/): stack Jest/jest-expo e SDK 58; preservar a base já configurada.
- [TanStack Query: React Native](https://tanstack.com/query/latest/docs/framework/react/react-native): AppState/focusManager, onlineManager e cleanup.
- [Zod: basics](https://zod.dev/basics): safeParse em dados desconhecidos e tratamento explícito de erro.
- [OpenAPI TypeScript](https://openapi-ts.dev/introduction): geração de tipos de documento real, sem validação runtime implícita.

Consulta de documentação não substitui validação das versões instaladas ou do contrato da API do projeto.
