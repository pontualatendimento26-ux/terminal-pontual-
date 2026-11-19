# Central Neural da IA

Tela de gestão do que a assistente sabe. Fica em `/central/neural/` e só abre para administrador e
superadministrador (todas as rotas exigem `config.edit`; o perfil das atendentes exige também
`usage.read.individual` e a leitura entra na auditoria).

## Onde está cada parte

| Parte | Arquivo |
|---|---|
| Rotas `/api/v1/neural/...` | `src/server/Pontual.Server/Api/NeuralEndpoints.cs` |
| Tetos, busca, divisão em partes, leitura de PDF/DOCX/TXT, ferramentas da IA | `Assistant/Knowledge.cs` |
| Texto enviado à IA (base inteira ou índice) | `Assistant/AssistantPrompt.cs` |
| Chamada ao Claude (fluxo, laço de consulta à base, resposta em JSON) | `Assistant/AssistantModel.cs` |
| Análise das conversas e serviço automático | `Assistant/AssistantAnalysis.cs` |
| Avaliação da resposta pelo chat (`POST /device/assistant/feedback`) | `Api/AssistantEndpoints.cs` |
| Mapa (Canvas 2D, sem biblioteca) | `wwwroot/central/neural/mapa.js` |
| Telas | `wwwroot/central/neural/index.html`, `neural.css`, `neural.js` |
| Testes | `tests/server/Pontual.Server.Tests/Api/NeuralTests.cs` |

## Dados

Roteiros, documentos e conhecimentos operacionais ficam na mesma tabela, `AssistantScripts`, separados
pela coluna `Kind` (`script`, `document`, `knowledge`). Tabelas novas: `KnowledgeLinks` (ligações),
`KnowledgeSuggestions` (sugestões), `AssistantFeedback` (avaliações do chat) e `AttendantInsights`
(perfil de cada atendente). O arquivo original de cada documento fica em `data/documentos/<id>.<ext>`.

`DbInitializer` cria as tabelas que faltam e acrescenta as colunas novas com `ALTER TABLE`; nenhuma
linha antiga é apagada.

## Como a IA lê a base

- **Leitura completa:** enquanto o texto ativo soma até `KnowledgeLimits.InlineBudget` (200 mil
  caracteres), a base inteira vai nas instruções de cada pergunta, em cache. A resposta chega em fluxo.
- **Índice + consulta:** acima disso, a IA recebe só o índice numerado (título, resumo, palavras-chave)
  e usa duas ferramentas: `abrir_conhecimento` (por número e parte) e `buscar_na_base` (busca por
  palavras, sem acento). Cada pergunta tem teto de 12 consultas e 150 mil caracteres abertos.
- Não há embeddings nem outro fornecedor de IA. A busca é textual, feita no servidor.

## Aprendizado

- A atendente marca "Ajudou" ou "Não ajudou" em cada resposta. "Não ajudou" com texto vira uma
  sugestão do tipo correção.
- A análise das conversas lê só as falas novas de cada atendente (a última fala analisada fica em
  `AttendantInsights.AnalyzedThroughMessageId`), atualiza o perfil e cria sugestões de lacuna,
  informação nova ou conteúdo a revisar. Uma lacuna repetida soma ocorrências em vez de duplicar.
- A análise automática roda no máximo uma vez a cada 20 horas por atendente, só com 4 falas novas ou
  mais e depois de 20 minutos de conversa parada. Desliga-se na aba Aprendizado.
- Nenhuma sugestão chega à IA sem aprovação: aprovar cria um conhecimento novo ou acrescenta o texto
  a um que já existe.

## Pendências conhecidas

- **PDF escaneado:** o arquivo é guardado com `FileStatus = no_text`, sem texto e desligado. Falta
  ligar o leitor de PDF escaneado (uma API própria da Pontual), que deve preencher `Content` e mudar
  o estado para `processed`.
- **Busca semântica:** não existe; a busca é por palavras.
- **Ligações entre conhecimentos:** organizam o mapa e aparecem nos detalhes; não mudam o que a IA responde.

## Publicar na Central

A Central roda como tarefa agendada do Windows na conta SISTEMA, então a troca precisa de administrador:

1. `dotnet publish src/server/Pontual.Server -c Release -r win-x64 --self-contained true -o dist/_central-nova`
2. Dois cliques em `dist\Atualizar a Central.cmd` (copia a pasta atual para `backup-central-<data>`,
   troca os arquivos, religa e confere).
