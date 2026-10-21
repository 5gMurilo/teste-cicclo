# Validação nativa e acessibilidade (gate N1)

**Criado:** 2026-10-21. **Última atualização:** 2026-10-21 (roteiro adaptado para iOS).

**Quem executa:** o usuário, no simulador ou no aparelho. O agente prepara este roteiro e só registra os resultados que o usuário reportar. Um export bem-sucedido (`pnpm --filter @cicclo/mobile build`) prova que o bundle compila, não que o app funciona: nenhum cenário abaixo está aprovado por inferência.

**Plataforma deste ensaio:** iOS (decisão do usuário em 2026-10-21, por ser mais simples de executar). O app é híbrido e o código é o mesmo nas duas plataformas, mas a spec cita Android (UX-02 e QUAL-02). O resultado vale como **"validado no iOS"**; o Android só será declarado validado se for ensaiado também (seção "Android, opcional" no fim).

**Estado:** APROVADO PELO USUÁRIO NO iOS em relato global (2026-10-21, "Validado o iOS"), **sem registro por cenário**, aparelho, versão do iOS, leitor de tela ou texto ampliado. Android não ensaiado. Para detalhar, o usuário pode informar uma linha por cenário e os dados da identificação.

## Pré-requisitos

1. API de negócio no ar com a base demo (carteira em 5000, serviços 1890 e 2090). Sobe pelo backend (`apps/api/scripts/start-demo.sh`); o retorno ao estado inicial é o modo `--reset-demo`, com a API parada antes. Seguir o README do backend; este roteiro não repete os comandos. A demo atual pode estar com o saldo já debitado (3110) por ensaios anteriores: para os valores abaixo, restaurar antes.
2. Subir o app informando a URL da API. Na raiz do repositório:

   ```sh
   EXPO_PUBLIC_API_URL=http://127.0.0.1:5001 pnpm --filter @cicclo/mobile ios
   ```

   - Simulador iOS: o comando abre o app no simulador; `127.0.0.1` alcança a API do próprio Mac.
   - iPhone físico: `pnpm --filter @cicclo/mobile dev`, ler o QR code com o Expo Go e usar `EXPO_PUBLIC_API_URL=http://<IP do Mac na rede local>:5001`, com o aparelho na mesma rede.
   - Sem a variável, as leituras falham e o app mostra erro; ela é obrigatória.
3. Registrar a identificação do ensaio abaixo antes de começar.

## Identificação do ensaio

| Campo | Valor |
| --- | --- |
| Data | 2026-10-21 (relato do usuário) |
| Simulador ou aparelho e versão do iOS | não informado (havia um simulador iPhone 17 ligado) |
| Leitor de tela usado | não informado |
| Commit do app | não informado pelo usuário (HEAD do repositório quando o relato foi dado: `b80fe36`, registro do agente) |
| URL da API usada (sem credenciais) | não informada pelo usuário (as instruções passadas usavam `http://127.0.0.1:5001`) |
| Saldo inicial | não informado |

## Cenários

Marcar cada cenário como PASS, FAIL ou BLOCKED (sem aparelho, sem API ou ferramenta indisponível), com observação. BLOCKED nunca conta como PASS.

### 1. Abas e Home inicial

1. Abrir o app sem deep link.
2. Esperado: a Home abre primeiro; as abas aparecem na ordem Serviços, Home, Checkout; a navegação entre as três funciona.

### 2. Header e layout

1. Observar o topo em cada aba.
2. Esperado: saudação `Olá! {nome}` à esquerda (Visitante se o nome não estiver configurado) e `Saldo da carteira` com `R$ 50,00` à direita; a barra de status, a ilha dinâmica (Dynamic Island) ou o notch e a barra home não cobrem o conteúdo; não há espaço duplicado no topo.

### 3. Compra controlada (5000 para 3110)

1. Na Home, adicionar uma lavagem (R$ 18,90). Em Checkout, conferir subtotal R$ 18,90, saldo confirmado R$ 50,00 e saldo estimado R$ 31,10, e o aviso de cobrança imediata e máquinas simuladas.
2. Confirmar a contratação. **Consome saldo da demo**: só em ambiente controlado.
3. Esperado: tela de contratação confirmada; header em R$ 31,10; seleção limpa; o botão "Ver contratação" leva ao detalhe.
4. Depois do cenário, restaurar o saldo pelo reset da demo (API parada).

A compra real está habilitada (gate C2 provado contra a API real em 2026-10-21).

### 4. Seleção e badge

1. Adicionar duas lavagens e uma secagem dentro do saldo; tentar uma adição que excede o saldo.
2. Esperado: o badge de Checkout soma as quantidades; o nome acessível da aba inclui a quantidade; o excesso mostra mensagem de saldo insuficiente e mantém a seleção; a seleção sobrevive à troca de abas.
3. Com o VoiceOver ligado, observar se a mensagem de bloqueio (por exemplo "Saldo insuficiente...") é falada quando aparece. Se não for, registrar como achado: o anúncio automático foi pensado primeiro para o Android.

### 5. Histórico e detalhe

1. Abrir Serviços; alternar Em andamento e Histórico; abrir uma contratação.
2. Esperado: contratações em ordem decrescente de data; lote misto continua em andamento; detalhe mostra preço histórico e estado por item; voltar (botão "Voltar para Serviços" ou gesto de arrastar da borda esquerda) leva a Serviços sem perder a seleção.

### 6. Rede e retry explícito

1. Desligar a rede: no aparelho, modo avião; no simulador, desligar o Wi-Fi do Mac. Observar o header e tentar adicionar um serviço.
2. Religar a rede.
3. Esperado: offline, o saldo aparece como desatualizado e adições ficam bloqueadas com explicação; ao reconectar, carteira e catálogo são relidos e só então a seleção volta a ser liberada; nenhuma compra é enviada sozinha. Se a leitura falhar, o botão de atualizar saldo refaz a leitura.

### 7. Suspensão e retomada

1. Com uma seleção montada, mandar o app para segundo plano por alguns segundos (gesto ou botão home) e voltar.
2. Esperado: ao voltar, carteira e catálogo são relidos; a seleção continua; nenhuma contratação é criada ou repetida.

### 8. Acessibilidade e texto ampliado

1. Ajustar o tamanho do texto ao máximo: Ajustes > Acessibilidade > Tela e Tamanho do Texto > Texto Maior, com "Tamanhos de Acessibilidade Maiores" ligado e o controle no máximo. Ligar o VoiceOver (Ajustes > Acessibilidade > VoiceOver; no simulador, o Accessibility Inspector do Xcode substitui a voz).
2. Percorrer as três abas, a revisão, a lista e o detalhe.
3. Esperado: saudação, saldo e ações permanecem legíveis, com quebra de linha ou rolagem quando necessário; nada fica cortado ou sobreposto; botões e abas são anunciados com nome e estado coerentes; mensagens de erro e bloqueio são anunciadas.

## Resultados

**Relato global do usuário em 2026-10-21: "Validado o iOS".** É o único registro. O usuário não informou resultado por cenário; por isso nenhum cenário abaixo está marcado como aprovado individualmente.

| Cenário | Resultado | Observações |
| --- | --- | --- |
| 1. Abas e Home inicial | SEM REGISTRO POR CENÁRIO | Coberto apenas pelo relato global acima |
| 2. Header e layout | SEM REGISTRO POR CENÁRIO | Coberto apenas pelo relato global acima |
| 3. Compra controlada | SEM REGISTRO POR CENÁRIO | Coberto apenas pelo relato global acima |
| 4. Seleção e badge | SEM REGISTRO POR CENÁRIO | Coberto apenas pelo relato global acima |
| 5. Histórico e detalhe | SEM REGISTRO POR CENÁRIO | Coberto apenas pelo relato global acima |
| 6. Rede e retry explícito | SEM REGISTRO POR CENÁRIO | Coberto apenas pelo relato global acima |
| 7. Suspensão e retomada | SEM REGISTRO POR CENÁRIO | Coberto apenas pelo relato global acima |
| 8. Acessibilidade e texto ampliado | SEM REGISTRO POR CENÁRIO | Coberto apenas pelo relato global acima |

Falhas encontradas viram tarefas corretivas atômicas, cada uma com teste de regressão, e o cenário falho é reexecutado antes de fechar este gate.

## Android, opcional

Para declarar também o Android, repetir os oito cenários com: emulador Android (`EXPO_PUBLIC_API_URL=http://10.0.2.2:5001 pnpm --filter @cicclo/mobile android`) ou aparelho (`http://<IP do Mac>:5001`), TalkBack no lugar do VoiceOver e fonte do sistema em 200% no lugar do texto ampliado do iOS. Enquanto isso não for feito, a entrega fica registrada como "validada no iOS".

## Limites deste registro

- O aprovado é a palavra do usuário sobre a execução, sem captura nem observações; vale como o gate N1 do AD-005 (executado pelo usuário), restrito ao iOS (AD-006).
- Não há registro de que VoiceOver, Texto Maior, modo avião e o cenário de compra tenham sido exercitados um a um; nenhum cenário individual é afirmado.
- Android, TalkBack e fonte 200% não foram ensaiados.
