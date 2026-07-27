# Migração para o servidor de transcrição — mapa do lado do Click Write

Leitura do documento **Integração do piloto com o servidor**
(`clickwrite-transcricao-server/INTEGRACAO-PILOTO.md`, contrato 2.0) cruzada com o
código deste repositório. Aqui está **onde encostar**: o que sai, o que entra, o
que precisa ser decidido antes da primeira linha.

O README continua sendo a especificação do produto; este arquivo é o roteiro de
uma migração específica e some quando ela terminar.

---

## Estado: 1ª parte FEITA

O piloto já **só grava e envia**: os dois canais vão para o servidor e o que volta é
exibido. Foram entregues os blocos 0 a 7 deste documento — `Piloto.Remote`, a identidade
da ligação, a classificação de erro na fila, a limpeza do que ficou obsoleto, as duas
dívidas e o mapeamento guardado pelas flags de `/v1/saude`.

O que **não** foi feito, e por quê:

- **Persistir em duas etapas** (registro salvo em `transcrito`, atualizado em `concluido`).
  Escolhida a opção (a) da seção 2.4: o `RemoteTranscriber` espera `transcrito` e, quando
  `resumoDisponivel` for ligado no servidor, encadeia a espera por `concluido` sozinho —
  sem recompilar. A persistência em duas etapas só paga quando o resumo do servidor existir
  **e** demorar o bastante para incomodar.
- **Token em texto claro** no `appsettings.json` (decisão 2 no fim deste arquivo). Continua
  aberta.
- **TLS** entre piloto e servidor (decisão 4). Continua aberta, e é a que precisa de
  resposta antes de produção.
- **Remover `TranscriptSanitizer`, `RuleExtractor`, `ContactMerger`, `GroundingChecker` e o
  LLM local.** Não é para fazer: eles são a única extração que existe enquanto
  `analiseDisponivel`/`resumoDisponivel` forem `false`. Saem quando o servidor assumir.

Ainda não exercitado (precisa de servidor real, não de CI): upload de ligação longa, queda
no meio do envio, e o reenvio idempotente depois de reiniciar o app.

> **O que a migração é:** tirar o Whisper da máquina do atendente.
> **O que ela não é:** tirar a extração e o resumo. Com `analiseDisponivel` e
> `resumoDisponivel` em `false` (estado de hoje), `RuleExtractor`,
> `ContactMerger`, `GroundingChecker`, `TranscriptSanitizer` e o LLM local
> continuam sendo a única extração que existe (INTEGRACAO §9).

---

## Bloco 0 — os dois bloqueadores (antes de escrever o `RemoteTranscriber`)

Não são detalhes de implementação: são coisas que o código **não tem** e das
quais a integração depende. Se forem deixados para depois, o resultado é uma
integração que funciona no teste e perde ligação em campo.

### 0.1 Não existe `ligacaoId` estável — e ele é a `Idempotency-Key`

INTEGRACAO §3.1 manda usar o `ligacaoId` como `Idempotency-Key` em todo POST. Hoje:

| Candidato | Por que não serve |
|---|---|
| `QueueItem.Id` | `INTEGER AUTOINCREMENT` local — serve, mas é do banco, não da ligação |
| `CallRecord.Uuid` ([CallRecord.cs:11](../src/Piloto.Core/Models/CallRecord.cs:11)) | nasce **no fim** do pipeline, depois da transcrição — tarde demais |
| `AudioCapture` ([AudioCapture.cs](../src/Piloto.Core/Models/AudioCapture.cs)) | não tem identificador nenhum |

**O que fazer:** um `LigacaoId` (GUID) criado no **encerramento da captura**, que
viaja `AudioCapture → QueueItem → CallRecord.Uuid`.

- `AudioCapture`: nova propriedade `LigacaoId` (default `Guid.NewGuid().ToString("N")`);
- `QueueItem`: nova propriedade + **coluna persistida** (migração v3, abaixo);
- `CallEnqueuer.Enfileirar` ([CallEnqueuer.cs:17](../src/Piloto.Core/Services/CallEnqueuer.cs:17)): grava;
- `QueueProcessor.ReconstruirCaptura` ([QueueProcessor.cs:286](../src/Piloto.Core/Pipeline/QueueProcessor.cs:286)): devolve;
- `TranscriptionPipeline.ProcessarAsync`: usa como `CallRecord.Uuid` — a ligação
  passa a ter **um** identificador do microfone ao banco.

Persistir é o que faz a retentativa **depois de reiniciar o app** reaproveitar o
job em vez de gastar uma segunda passada de GPU. É também o que resolve, de
graça, o caso "app fechou enquanto o servidor processava": o re-POST com a mesma
chave devolve o mesmo `jobId`.

> `CallEnqueuer.Reprocessar` ([CallEnqueuer.cs:36](../src/Piloto.Core/Services/CallEnqueuer.cs:36))
> precisa do oposto: **novo** `LigacaoId`. Reprocessar existe para retestar com
> outro modelo/versão; com a chave antiga o servidor devolveria o resultado
> velho e o botão não faria nada visível.

### 0.2 A fila trata falha de rede como falha definitiva

[QueueProcessor.cs:159-168](../src/Piloto.Core/Pipeline/QueueProcessor.cs:159) —
**qualquer** exceção incrementa `Tentativas`; em 3 o item vai para `Erro` e
`MaterializarItensComErro` cria um registro marcado para revisão. O laço ocioso
gira a cada 3 s ([IntervaloOcioso](../src/Piloto.Core/Pipeline/QueueProcessor.cs:23)).

Resultado com o servidor fora do ar: **~10 segundos de indisponibilidade queimam
as três tentativas** e a ligação vira "erro" — exatamente o que INTEGRACAO §0.1
proíbe ("o piloto precisa enfileirar e reenviar, não descartar"). Hoje isso não
dói porque o Whisper é local e não cai; depois da migração é o modo de falha
mais provável do sistema.

**O que fazer:** classificar a falha na origem, no `RemoteTranscriber`, e deixar
a fila reagir a ela (INTEGRACAO §8):

| Classe | Origem | Reação da fila |
|---|---|---|
| **Definitiva** | `400`, `401`, `413`, `415` | `Tentativas = Max` direto → `Erro` com o motivo. Retentar só repete |
| **Transitória** | timeout, conexão recusada, `5xx` | **não conta tentativa**; agenda nova tentativa com recuo (30 s → 2 min → 10 min, teto ~30 min) |
| **Reenvio** | `404` no GET (passou dos 900 s) | volta a `Pendente` **sem** contar tentativa — o áudio ainda está em disco |
| **Erro do job** | `estado: "erro"` | conta tentativa (é processamento, não rede); novo POST cria job novo |

Implica:

- exceções tipadas em `Piloto.Remote` (`ServidorIndisponivelException`,
  `ServidorRecusouException`, `ResultadoExpiradoException`);
- `QueueItem.ProximaTentativaEm` (coluna, migração v3);
- `ICallRepository.ProximoPendente` ([ICallRepository.cs](../src/Piloto.Core/Abstractions/ICallRepository.cs))
  passa a filtrar `proxima_tentativa_em IS NULL OR <= agora`;
- `RecuperarItensOrfaos(maxTentativas)` mantém a regra atual (queda do processo
  **é** motivo para contar tentativa — protege contra loop de crash).

### 0.3 Migração v3 do banco

[MigrationRunner.cs:12](../src/Piloto.Data/MigrationRunner.cs:12) — acrescentar
uma entrada ao array (o runner versiona por `PRAGMA user_version`, nada mais a fazer):

```sql
ALTER TABLE queue ADD COLUMN ligacao_id           TEXT;
ALTER TABLE queue ADD COLUMN proxima_tentativa_em TEXT;
ALTER TABLE queue ADD COLUMN job_id               TEXT;   -- opcional, ver 2.4
```

Itens antigos ficam com `NULL`: `ligacao_id` nulo → gera um na hora de enviar;
`proxima_tentativa_em` nulo → elegível agora. Nenhum registro existente quebra.

---

## Bloco 1 — configuração e saúde

### 1.1 `AppSettings` — entra `servidor`, sai `whisper`

[AppSettings.cs](../src/Piloto.Core/Configuration/AppSettings.cs):

```csharp
public sealed class ServidorSettings
{
    public string Url { get; set; } = "http://DESKTOP-VEP5JQ3:8600";
    public string Token { get; set; } = "";
    public int TimeoutSegundos { get; set; } = 300;   // > 120 s do long-poll
    public int MaxTentativas { get; set; } = 3;
}
```

| Sai | Onde |
|---|---|
| `WhisperSettings` (classe inteira) | [AppSettings.cs:84](../src/Piloto.Core/Configuration/AppSettings.cs:84) |
| `AppSettings.Whisper` | linha 14 |
| `CaminhoModeloWhisper` | linha 36 |
| bloco `"whisper"` | [config/appsettings.json:4](../config/appsettings.json:4) |
| `FilaSettings.PrioridadeProcesso` | linha 112 — ver 4.4 |

`PastaModelos` **fica**: o LLM local continua morando lá.

> **Armadilha do `ConfigService`.** [ConfigService.cs:34-39](../src/Piloto.App/Services/ConfigService.cs:34)
> só copia o `appsettings.json` empacotado quando o arquivo do usuário **não
> existe**. Toda máquina já instalada tem o dela — e não vai receber o bloco
> `servidor` na atualização. O app subirá com os defaults da classe (URL do
> desenvolvimento, token vazio → `401`). Escolha uma:
> **(a)** merge na subida (chave ausente no arquivo do usuário é escrita a
> partir do default) — é a solução geral e serve para o próximo bloco também;
> **(b)** tela de configuração obrigando o preenchimento na primeira execução
> pós-atualização.
> Sem uma das duas, a atualização em campo sobe muda.

### 1.2 `GET /v1/saude` na subida

Novo, em `Piloto.Remote` (ver 2.1):

```csharp
public sealed record ServidorSaude(
    bool Ok, string VersaoContrato, string? Modelo, string? Device,
    bool ModeloCarregado, int Pendentes, int Processando,
    bool AutenticacaoAtiva, bool AnaliseDisponivel, bool ResumoDisponivel);
```

- chamada em [App.xaml.cs:63-99](../src/Piloto.App/App.xaml.cs:63), antes de
  `queue.Iniciar()`, **sem bloquear a subida** (a UI abre; o resultado atualiza o banner);
- `VersaoContrato != "2.0"` → avisa e **não** interpreta `dialogo`/`campos`/`resumo`;
- `AnaliseDisponivel`/`ResumoDisponivel` guardam o mapeamento do Bloco 5 —
  guardar por **flag**, nunca por versão (INTEGRACAO §5);
- releitura periódica (a cada N minutos, ou a cada item da fila) — o servidor
  pode ligar as capacidades sem o app reiniciar.

### 1.3 Banner e status

[MainWindow.xaml.cs:149](../src/Piloto.App/MainWindow.xaml.cs:149) `AtualizarBanner()`
hoje diz "Modelos ausentes… baixe com download-models.ps1". Passa a cobrir:

- servidor inalcançável → "Servidor de transcrição fora do ar. As ligações estão
  sendo gravadas e serão enviadas quando ele voltar." (a mensagem tem de dizer
  que **nada se perde** — é a diferença entre o atendente confiar e não confiar);
- `401` → "Token inválido — configure em Configurações";
- `versaoContrato` diferente → aviso explícito;
- LLM local ausente (se `llm.habilitado`) → mensagem atual, reduzida ao LLM.

### 1.4 Tela de configurações

[SettingsWindow.xaml.cs](../src/Piloto.App/Views/SettingsWindow.xaml.cs) +
[SettingsWindow.xaml](../src/Piloto.App/Views/SettingsWindow.xaml):

| Sai | Entra |
|---|---|
| `TxtWhisperModelo` (linhas 37, 90) | `TxtServidorUrl` |
| `TxtWhisperThreads` (linhas 38, 91) | `TxtServidorToken` (senha) |
| "Whisper: presente/AUSENTE" (linha 50) | botão **Testar conexão** → `GET /v1/saude`, mostra modelo, device, fila e as duas flags |
| "Pipeline pausado — baixe/aponte os modelos" (linha 55) | estado do servidor + estado do LLM local |

---

## Bloco 2 — o `RemoteTranscriber`

### 2.1 Projeto novo `src/Piloto.Remote`

Não cabe em `Piloto.Transcription` (que carrega `Whisper.net` e vai encolher) nem
em `Piloto.Core` (que é livre de I/O de rede). Acrescentar à
[Piloto.sln](../Piloto.sln) e ao build.

```
src/Piloto.Remote/
  Piloto.Remote.csproj          // net8.0, ref → Piloto.Core
  ClickWriteServerClient.cs     // HttpClient: saude, POST, GET com long-poll
  RemoteTranscriber.cs          // ITranscriber
  ServidorExceptions.cs         // definitiva | transitória | expirado
  Contrato/                     // DTOs do contrato 2.0 (System.Text.Json)
    RespostaSaude.cs  RespostaJob.cs  Resultado.cs
    Canal.cs  Segmento.cs  Dialogo.cs  Campos.cs  ResumoServidor.cs
```

`HttpClient` **único e reutilizado** (`SocketsHttpHandler`, `PooledConnectionLifetime`),
`Timeout` maior que os 120 s do long-poll.

### 2.2 A costura

[ITranscriber.cs](../src/Piloto.Core/Abstractions/ITranscriber.cs) **não muda** —
é o acerto do desenho. `LiberarModelo()` fica no default (`false`).

[CompositionRoot.cs:48](../src/Piloto.App/Services/CompositionRoot.cs:48):

```diff
- services.AddSingleton<ITranscriber, WhisperTranscriber>();
+ services.AddSingleton<ITranscriber, RemoteTranscriber>();
```

O `WhisperTranscriber.cs` **fica no repositório** (INTEGRACAO §2) — preserva o
histórico dos filtros calibrados em campo. Sai só o registro no DI.

### 2.3 A sequência (INTEGRACAO §3)

```
POST /v1/transcricoes                              multipart, Idempotency-Key = ligacaoId
  ├─ atendente / cliente   arquivo (pelo menos um)
  ├─ ligacaoId             texto
  ├─ metadados             JSON  (CallMetadata)
  ├─ listas                JSON  (ListasFechadas — já sai com os nomes certos, ver 5.4)
  └─ glossario             texto (Func<string?> glossarioProvider, já no DI)
       → 202 { jobId, estado, posicaoNaFila }

GET  /v1/transcricoes/{jobId}?esperarSegundos=30&esperarAte=transcrito   ← fase 1
GET  /v1/transcricoes/{jobId}?esperarSegundos=30&esperarAte=concluido    ← fase 2
```

O laço repete a chamada enquanto o estado for anterior ao alvo (referência
executável: `esperar()` em `scripts/enviar.py` do servidor).

### 2.4 A espera em duas fases e o que o pipeline hoje não permite

Aqui há um descompasso a decidir. O contrato quer: **mostrar e persistir em
`transcrito`**, completar com o resumo em `concluido`. O
[TranscriptionPipeline.ProcessarAsync](../src/Piloto.Core/Pipeline/TranscriptionPipeline.cs:61)
é síncrono de ponta a ponta e só persiste no fim (`QueueProcessor` chama
`SalvarRegistro` depois de tudo).

Duas saídas:

- **(a) mínima, recomendada para a primeira entrega.** O `RemoteTranscriber`
  espera `transcrito` e devolve o `Transcript`. Com `resumoDisponivel: false` o
  bloco `resumo` do servidor não existe mesmo, e o resumo continua sendo o LLM
  local, já dentro do pipeline. **A fase 2 não é necessária hoje** — o que a
  torna necessária é o servidor ligar o resumo (bloco C do PRONTIDAO).
  Deixar o código do long-poll pronto para `esperarAte=concluido`, sem usar.
- **(b) completa.** Persistir em duas etapas (registro salvo em `transcrito`,
  atualizado em `concluido`). Exige `QueueProcessor` reentrante e evento
  `RegistroProcessado` disparado duas vezes. É trabalho real e **não paga hoje**.

Anotar a escolha aqui quando for feita. Se for (a), o `job_id` da migração v3 é
dispensável.

### 2.5 Prazo de 900 s

O resultado expira 900 s depois de pronto (INTEGRACAO §3.3). O long-poll ativo
não deixa passar disso — o risco é o app fechar no meio. Nesse caso: `404` no
GET → item volta a `Pendente` sem contar tentativa → re-POST com a mesma
`Idempotency-Key`. Sem o `ligacaoId` persistido (0.1) isso não funciona.

---

## Bloco 3 — a fila e o que ela ganha/perde

[QueueProcessor.cs](../src/Piloto.Core/Pipeline/QueueProcessor.cs):

| Linha | Hoje | Depois |
|---|---|---|
| [:87](../src/Piloto.Core/Pipeline/QueueProcessor.cs:87) `if (!_modelos.PipelinePronto)` | pausa sem Whisper | **não pausa por servidor fora do ar** — tenta, falha transitória, recua. Pausar deixaria o item parado sem evidência |
| [:80](../src/Piloto.Core/Pipeline/QueueProcessor.cs:80) `Thread.Priority = Lowest` | disputa CPU com o Chrome | sem sentido — a espera é I/O. Remover |
| [:28](../src/Piloto.Core/Pipeline/QueueProcessor.cs:28) `DescargaAposOciosidade` | descarrega Whisper **e** LLM | vale só para o LLM local (fica) |
| [:30](../src/Piloto.Core/Pipeline/QueueProcessor.cs:30) `MaxTentativas = 3` | conta tudo | conta só falha **definitiva** e de processamento (0.2). Passa a vir de `servidor.maxTentativas` |
| [:23](../src/Piloto.Core/Pipeline/QueueProcessor.cs:23) `IntervaloOcioso = 3 s` | fila local | mantém para a fila; o recuo de rede é por item |
| [:190](../src/Piloto.Core/Pipeline/QueueProcessor.cs:190) `TentarResumoPendenteAsync` | cura resumos que faltaram | **fica** enquanto o resumo for local |
| [:249](../src/Piloto.Core/Pipeline/QueueProcessor.cs:249) `MaterializarItensComErro` | ligação some se estourar tentativas | fica, com o motivo agora vindo do servidor (`400`/`413`/`415` são autoexplicativos) |

Ganho colateral: `Simultaneas = 1` ([FilaSettings](../src/Piloto.Core/Configuration/AppSettings.cs:109))
existia porque duas passadas de Whisper na mesma máquina se atropelavam. Com o
peso no servidor, 2–3 envios simultâneos são viáveis — **mas não na primeira
entrega**: o servidor tem fila própria e a idempotência ainda não foi exercitada
sob concorrência.

---

## Bloco 4 — o que fica obsoleto

Tudo aqui existe por causa do Whisper local. INTEGRACAO §0.3: "é a parte mais
frágil do cliente hoje".

### 4.1 Remoção direta

| Arquivo / membro | Situação |
|---|---|
| `WhisperTranscriber` no DI ([CompositionRoot.cs:48](../src/Piloto.App/Services/CompositionRoot.cs:48)) | **sai** (o arquivo fica) |
| [`Hardware.CpuComportaBeam`](../src/Piloto.Core/Services/Hardware.cs:21) | único consumidor é o `WhisperTranscriber` → morre com ele |
| `Hardware.ResolverThreads` | **fica** — o LLM local usa |
| [`MemoriaDisponivel`](../src/Piloto.Core/Services/MemoriaDisponivel.cs) | **fica** — `LlmWorkerExtractor.cs:389,415` ainda usa |
| [`TranscriptionPipeline.MemoriaComportaLlmSemLiberarWhisper`](../src/Piloto.Core/Pipeline/TranscriptionPipeline.cs:209) | **sai** — não há Whisper para liberar. Some com os dois `if` de :93 e :181 |
| [`TranscriptionPipeline.LiberarModelos`](../src/Piloto.Core/Pipeline/TranscriptionPipeline.cs:54) | vira só `_llm.LiberarModelo()` |
| `whisper` em [config/appsettings.json](../config/appsettings.json:4) e `WhisperSettings` | **sai** (1.1) |
| `prioridadeProcesso` ([AppSettings.cs:112](../src/Piloto.Core/Configuration/AppSettings.cs:112)) | **sai** — nunca teve leitor além do nome |

### 4.2 `IModelCatalog` encolhe

[IModelCatalog.cs](../src/Piloto.Core/Abstractions/IModelCatalog.cs) /
[ModelCatalog.cs](../src/Piloto.Core/Services/ModelCatalog.cs):

| Membro | Destino |
|---|---|
| `WhisperDisponivel`, `CaminhoWhisper`, `CandidatosWhisper` | **saem** |
| `PipelinePronto` | `!Llm.Habilitado \|\| LlmDisponivel` — o "pronto" do transcritor passa a ser o `/v1/saude` |
| `ModelosAusentes()` | só o LLM |
| `LlmDisponivel`, `CaminhoLlm`, `CandidatosLlm` | ficam |

Consumidores a ajustar: `MainWindow.xaml.cs:151,156`, `SettingsWindow.xaml.cs:50,55`,
`QueueProcessor.cs:87`, `TranscriptionPipeline` (construtor).

### 4.3 `EscolherModelo`/`LiberarModelo` do Whisper

Ficam dentro do `WhisperTranscriber.cs` preservado. Não referenciar de fora.

### 4.4 Instalação: ~600 MB a menos

- [scripts/download-models.ps1](../scripts/download-models.ps1): a seção `$modelos`
  (Whisper) inteira e o parâmetro `-Whisper` saem; o `$llms` fica. Cuidado com a
  **limpeza de obsoletos** (linhas 180-201): ela só apaga o que o script conhece —
  ao remover os nomes do Whisper de `$conhecidos`, os `.bin` já baixados nas
  máquinas passam a ser "não gerenciados" e ficam ocupando disco para sempre.
  Manter os nomes numa lista `$legado` só para apagar.
- [installer/setup.iss:70-73,97](../installer/setup.iss): o atalho "Baixar modelos
  (Whisper + Gemma)" muda de nome; se o LLM local for embutido depois, some.
- `Whisper.net`/`Runtime`/`Runtime.NoAvx` em
  [Piloto.Transcription.csproj](../src/Piloto.Transcription/Piloto.Transcription.csproj):
  **ficam por ora** (o arquivo preservado precisa compilar). Sair só quando o
  `WhisperTranscriber.cs` sair — decisão separada, depois de a integração provar
  que não se volta atrás.

---

## Bloco 5 — mapeamento dos dados

### 5.1 `Segmento` → `TranscriptSegment` (o que vale hoje)

[Transcript.cs](../src/Piloto.Core/Models/Transcript.cs):

| Servidor | C# | Nota |
|---|---|---|
| `inicio`, `fim` (double, s) | `Inicio`, `Fim` | `TimeSpan.FromSeconds` |
| `texto` | `Texto` | já vem `.Trim()`ado |
| `confianca` | `Confianca` | **escala diferente** — ver 5.5 |
| `probSemFala` | — | não existe no C#; ignorar |
| `canais[].speaker` | `Speaker` | `"atendente"` / `"cliente"` |

Concatenar os segmentos dos dois canais e chamar `new Transcript(...)`: a fusão
ordenada por tempo **já está no construtor** ([Transcript.cs:41](../src/Piloto.Core/Models/Transcript.cs:41)).
Não reimplementar.

### 5.2 Canal vazio não é erro

`canais[].vazio: true` + `motivoVazio` (INTEGRACAO §4). O
[TranscriptionPipeline.cs:151](../src/Piloto.Core/Pipeline/TranscriptionPipeline.cs:151)
já marca revisão para transcrição vazia — o ganho é usar o `motivoVazio` do
servidor como texto do motivo (ele diz *por quê*: "sem amostras de áudio (só
cabeçalho)"), e distinguir **um** canal vazio (normal, segue com o outro) de
**os dois** (falha de captura).

### 5.3 O que fica atrás das flags (INTEGRACAO §5 e §9)

Escrever os DTOs e o parsing de `dialogo`, `campos` e `resumo`; **não** ligar a
tela nem o banco enquanto `analiseDisponivel`/`resumoDisponivel` forem `false`.
Quando o servidor ligar as capacidades, o piloto usa sem recompilar.

Nome que **não** casa, para quando chegar a hora:
`campos.documentos` (servidor, CPF+CNPJ juntos com `tipo`) ≡
`ObjectiveFields.Cpfs` (C#, nome mantido pela compatibilidade do JSON persistido).

`resumo` do servidor mapeia 1:1 em [`LlmSummary`](../src/Piloto.Core/Models/LlmSummary.cs)
(`resumo`, `motivoContato`, `produto`, `status`, `pedido`, `proximoPasso`) — sem
surpresa. `avisos[]` do servidor → `registro.MarcarRevisao(...)`; a política de
revisão continua sendo do cliente (CONTRATO §avisos).

### 5.4 O que sobe: `metadados` e `listas`

- [`CallMetadata`](../src/Piloto.Core/Models/CallMetadata.cs) casa campo a campo
  com o contrato (`numero`, `ticketId`, `status`, `atendente`, `iniciadaEm`,
  `encerradaEm`, `emailCliente`, `telefoneCliente`, `nomeCliente`, `avisosCaptura`).
  **Exceção:** `OrigemJson` ([CallMetadata.cs:29](../src/Piloto.Core/Models/CallMetadata.cs:29))
  é o payload bruto da extensão, guardado para auditoria local. Cairia em
  `metadados.extra` e trafegaria PII duplicada sem nenhum uso no servidor —
  **excluir do envio** (`[JsonIgnore]` num DTO de envio, não na classe persistida).
- [`ListasFechadas`](../src/Piloto.Core/Configuration/ListasFechadas.cs) já
  serializa como `motivo_contato` / `produto` / `status`
  ([JsonPropertyName](../src/Piloto.Core/Configuration/ListasFechadas.cs:13)) —
  é exatamente o formato que o servidor espera. `Func<ListasFechadas>` já está no
  DI ([CompositionRoot.cs:37](../src/Piloto.App/Services/CompositionRoot.cs:37)) e
  relê o arquivo a cada uso: mudar `motivo_contato` continua sem exigir reinício,
  agora nem do servidor.
- `glossario`: `Func<string?>` já registrado ([CompositionRoot.cs:38](../src/Piloto.App/Services/CompositionRoot.cs:38)),
  hoje consumido pelo `WhisperTranscriber` — passa ao `RemoteTranscriber` sem mudança.

### 5.5 Os limiares de confiança (INTEGRACAO §7 — a seção que economiza tempo)

Medição do servidor sobre 20 ligações reais (249 min, 239 segmentos):

| Constante | Onde | Efeito medido | O que fazer |
|---|---|---|---|
| `ConfiancaMinima = 0.30f` | [WhisperTranscriber.cs:48](../src/Piloto.Transcription/WhisperTranscriber.cs:48) | descarta **1 de 239 (0,4%)** — inerte | não replicar no `RemoteTranscriber` |
| `LimiarBaixaConfianca = 0.55` | [Transcript.cs:18](../src/Piloto.Core/Models/Transcript.cs:18) | marca **8 de 239 (3,3%)**, mediana 0,754 | **fica** — é o único que ainda faz algo. Conferir numa amostra antes de confiar |
| `PadraoAlucinacao` | [WhisperTranscriber.cs:55](../src/Piloto.Transcription/WhisperTranscriber.cs:55) | **zero** casamentos no corpus | não portar |
| `ColapsarRepeticoes` | `WhisperTranscriber` | não pega loop **dentro** do segmento (o caso real, com confiança 0,90) | não portar |

`canais` vem **sem filtro nenhum** — matéria-prima. O filtro que resolve o loop
intra-segmento é do servidor (bloco B do PRONTIDAO). A conclusão prática: não
tratar "nada foi descartado" como sinal de qualidade.

`TranscriptSanitizer` e `GroundingChecker` **ficam** — operam sobre o texto, não
sobre a origem dele.

---

## Bloco 6 — as duas dívidas que o servidor já cobra (INTEGRACAO §10)

Pequenas, independentes, e fechá-las agora evita que a integração *pareça* estar
perdendo dado quando a análise do servidor for ligada.

### 6.1 `FieldType.Nome` + `ObjectiveFields.Nomes`

O servidor devolve `campos.nomes`; o C# não tem a categoria.

- [ObjectiveFields.cs:4](../src/Piloto.Core/Models/ObjectiveFields.cs:4): `Nome` no enum;
- linha 58: `public List<ExtractedValue> Nomes { get; init; } = new();`
- `Todos()` (:68), `PorCategoria()` (:72, título "Nomes"), `Chave()` (:117 —
  nome normaliza por `Trim()` + caixa);
- consumidores herdam de graça: `DetailWindow.xaml.cs:87` e o `RecordExporter`
  iteram `PorCategoria()`;
- JSON persistido: lista nova ausente desserializa vazia. Sem migração.

### 6.2 Ticket e nome no `ContactMerger`

[ContactMerger.Aplicar](../src/Piloto.Core/Pipeline/ContactMerger.cs:32) já traz
e-mail, telefone e o número do discador do cadastro. Faltam dois que chegam na
mesma `CallMetadata` e são simplesmente ignorados:

- `metadata.NomeCliente` → `Nomes`, `Origem = Extensao`, `TrechoOrigem = "cadastro do Zendesk"`;
- `metadata.TicketId` → `Protocolos`, `Origem = Extensao`, `TrechoOrigem = "ticket do Zendesk"`.

Ambos com `Confianca = 1.0` — é dado digitado, o atendente copia sem conferir.
Testes: [ContactMergerTests.cs](../tests/Piloto.Tests/ContactMergerTests.cs).

---

## Bloco 7 — documentação e build

- [README.md](../README.md) e [docs/estado-do-projeto.md](estado-do-projeto.md):
  a tabela "Implementado" ainda descreve transcrição local como camada do produto;
- [.github/workflows/build.yml](../.github/workflows/build.yml): `Piloto.Remote`
  entra na solução — o build compila "os 9 projetos" hoje, passa a 10;
- testes: `Piloto.Remote` pede testes de **parsing e de classificação de erro**
  (não de rede). O servidor tem `.\executar.ps1 -Falso`, que implementa o
  contrato **por inteiro** (diálogo, campos, resumo, avisos) sem GPU — é contra
  ele que o `RemoteTranscriber` se escreve e se testa em qualquer máquina,
  inclusive esta, sem hardware.

---

## Ordem sugerida

Cada passo é commitável e verificável sozinho; nenhum deixa o app quebrado.

| # | Passo | Depende de |
|---|---|---|
| 1 | **Dívidas do Bloco 6** (`FieldType.Nome`, ticket/nome no merger) | nada — puro `Piloto.Core`, com teste |
| 2 | **`LigacaoId` + migração v3 + `ProximaTentativaEm`** (0.1, 0.3) | nada — ainda sem uso, mas persistindo |
| 3 | **`ServidorSettings` + merge de config + `/v1/saude` + banner/tela** (Bloco 1) | 2 |
| 4 | **`Piloto.Remote`**: cliente, DTOs, exceções, `RemoteTranscriber` — contra `-Falso` | 3 |
| 5 | **Classificação de erro na fila** (0.2, Bloco 3) | 4 |
| 6 | **Troca no DI** — `RemoteTranscriber` entra, `WhisperTranscriber` sai do container | 5 |
| 7 | **Limpeza** (Bloco 4): `IModelCatalog`, memória, prioridade, config, instalador | 6 |
| 8 | **Parsing guardado pelas flags** (5.3) — sem tela, sem banco | 4 |

Só o passo 6 é irreversível na prática (a partir dele não há transcrição sem
servidor). Os passos 1, 2 e 8 podem ir para a `main` antes de o servidor estar
em produção.

---

## Precisa de decisão sua

1. **Espera em duas fases: (a) mínima ou (b) completa?** (2.4) — recomendo (a);
   a (b) só paga quando o servidor ligar o resumo.
2. **Token em claro no `appsettings.json`.** O arquivo fica em
   `%LOCALAPPDATA%\Piloto\config`, legível por qualquer processo do usuário.
   Alternativa: DPAPI (`ProtectedData`, escopo do usuário) — barato, e o token
   deixa de viajar em backup/print de tela. Decidir antes do passo 3, porque muda
   o formato gravado.
3. **Config existente nas máquinas já instaladas** (1.1) — merge automático ou
   preenchimento manual na atualização?
4. **O áudio passa a sair da máquina.** Rede local (`DESKTOP-VEP5JQ3`), HTTP sem
   TLS, com CPF/telefone/nome dentro. O servidor não persiste nada de propósito
   (INTEGRACAO §3.3), mas o tráfego é claro na LAN. Se isso precisa de TLS,
   é decisão de infraestrutura e vale saber **antes** de rodar em produção, não
   depois.
5. **Retenção de áudio local** (`retencaoDias.audio = 30`): agora o áudio é
   também o material de reenvio. 30 dias continua servindo — só confirmar que
   não vai encolher junto com a "economia" da migração.
