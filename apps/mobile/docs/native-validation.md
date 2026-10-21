# Validação Android e acessibilidade (gate N1)

**Criado:** 2026-10-21. **Última atualização:** 2026-10-21.

**Quem executa:** o usuário, com o aparelho ou emulador Android. O agente prepara este roteiro e só registra os resultados que o usuário reportar. Um export bem-sucedido (`pnpm --filter @cicclo/mobile build`) prova que o bundle compila, não que o app funciona no Android: nenhum cenário abaixo está aprovado por inferência.

**Estado:** PENDENTE. Nenhum cenário foi executado.

## Pré-requisitos

1. API de negócio no ar com a base demo (carteira em 5000, serviços 1890 e 2090). O backend documenta a subida com `apps/api/scripts/start-demo.sh` e o retorno ao estado inicial pelo modo `--reset-demo` (API parada antes). Seguir o README do backend; este roteiro não repete os comandos.
2. `apps/mobile/.env` a partir de `.env.example`, com `EXPO_PUBLIC_API_URL` alcançável pelo aparelho:
   - emulador Android: `http://10.0.2.2:5001`;
   - aparelho físico: `http://<IP da máquina na rede local>:5001`, na mesma rede.
3. Iniciar o app: `pnpm --filter @cicclo/mobile android`. O comando apenas abre o app; não prova nada.
4. Registrar a identificação do ensaio abaixo antes de começar.

## Identificação do ensaio

| Campo | Valor |
| --- | --- |
| Data | (preencher) |
| Aparelho ou emulador, versão do Android | (preencher) |
| Leitor de tela usado (TalkBack) e versão | (preencher) |
| Commit do app (`git rev-parse --short HEAD`) | (preencher) |
| URL da API usada (sem credenciais) | (preencher) |
| Saldo inicial | (esperado 5000 centavos, R$ 50,00) |

## Cenários

Marcar cada cenário como PASS, FAIL ou BLOCKED (sem aparelho, sem API ou ferramenta indisponível), com observação. BLOCKED nunca conta como PASS.

### 1. Abas e Home inicial

1. Abrir o app sem deep link.
2. Esperado: a Home abre primeiro; as abas aparecem na ordem Serviços, Home, Checkout; a navegação entre as três funciona.

### 2. Header e layout

1. Observar o topo em cada aba.
2. Esperado: saudação `Olá! {nome}` à esquerda (Visitante se o nome não estiver configurado) e saldo `R$ 50,00` à direita; barra de status e gestos de navegação não cobrem o conteúdo; não há inset duplicado.

### 3. Compra controlada (5000 para 3110)

1. Na Home, adicionar uma lavagem (R$ 18,90). Em Checkout, conferir subtotal R$ 18,90, saldo confirmado R$ 50,00 e saldo estimado R$ 31,10, e o aviso de cobrança imediata e máquinas simuladas.
2. Confirmar a contratação. **Consome saldo da demo**: só em ambiente controlado.
3. Esperado: tela de contratação confirmada; header em R$ 31,10; seleção limpa; botão para a contratação leva ao detalhe.
4. Depois do cenário, restaurar o saldo pelo reset da demo (API parada).

A compra real está habilitada (gate C2 provado contra a API real em 2026-10-21). Se a demo já foi usada, restaure o saldo antes.

### 4. Seleção e badge

1. Adicionar duas lavagens e uma secagem dentro do saldo; tentar uma adição que excede o saldo.
2. Esperado: o badge de Checkout soma as quantidades; o nome acessível da aba inclui a quantidade; o excesso mostra mensagem de saldo insuficiente e mantém a seleção; a seleção sobrevive à troca de abas.

### 5. Histórico e detalhe

1. Abrir Serviços; alternar Em andamento e Histórico; abrir uma contratação.
2. Esperado: contratações em ordem decrescente de data; lote misto continua em andamento; detalhe mostra preço histórico e estado por item; voltar leva a Serviços sem perder a seleção.

### 6. Rede e retry explícito

1. Desligar a rede do aparelho; observar o header e tentar adicionar um serviço.
2. Religar a rede.
3. Esperado: offline, o saldo aparece como desatualizado e adições ficam bloqueadas com explicação; ao reconectar, carteira e catálogo são relidos e só então a seleção volta a ser liberada; nenhuma compra é enviada sozinha. Se a leitura falhar, o botão de atualizar saldo refaz a leitura.

### 7. Suspensão e retomada

1. Com uma seleção montada, mandar o app para segundo plano por alguns segundos e voltar.
2. Esperado: ao voltar, carteira e catálogo são relidos; a seleção continua; nenhuma contratação é criada ou repetida.

### 8. Acessibilidade e fonte ampliada

1. Ajustar a fonte do sistema para o maior tamanho (200%) e ativar o TalkBack.
2. Percorrer as três abas, a revisão, a lista e o detalhe.
3. Esperado: saudação, saldo e ações permanecem legíveis, com quebra de linha ou rolagem quando necessário; nada fica cortado ou sobreposto; botões e abas são anunciados com nome e estado coerentes; mensagens de erro e bloqueio são anunciadas.

## Resultados

| Cenário | Resultado | Observações |
| --- | --- | --- |
| 1. Abas e Home inicial | PENDENTE | |
| 2. Header e layout | PENDENTE | |
| 3. Compra controlada | PENDENTE | |
| 4. Seleção e badge | PENDENTE | |
| 5. Histórico e detalhe | PENDENTE | |
| 6. Rede e retry explícito | PENDENTE | |
| 7. Suspensão e retomada | PENDENTE | |
| 8. Acessibilidade e fonte ampliada | PENDENTE | |

Falhas encontradas viram tarefas corretivas atômicas, cada uma com teste de regressão, e o cenário falho é reexecutado antes de fechar este gate.
