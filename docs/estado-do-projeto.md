# Estado do projeto — Click Write

> **1.1 — migração para o servidor de transcrição (1ª parte).** O piloto deixou de transcrever
> na máquina do atendente: agora ele grava os dois canais, envia ao servidor e exibe o que
> volta. O mapa completo da migração está em [migracao-servidor.md](migracao-servidor.md).

> **1.0** — o produto passou a se chamar **Click Write** (era "Piloto" até a 0.7.x).
> Renomeados: instalador, executável (`ClickWrite.exe`), pasta de instalação, telas e
> extensão. Mantidos de propósito: projetos/namespaces `Piloto.*`, a pasta de dados
> `%LOCALAPPDATA%\Piloto` e o mutex `PilotoAppMutex` — as razões estão no README.

Documento de acompanhamento do desenvolvimento. O README continua sendo a especificação;
aqui fica **o que já está implementado**, **como validar** e **o que observar**.

## Como este projeto é compilado

Esta máquina de desenvolvimento **não tem o .NET SDK instalado** (confirmado: `dotnet` ausente),
exatamente como o README descreve: *aqui você edita e versiona; o build/testes/instalador rodam
no GitHub Actions*.

Portanto o ciclo de validação é:

1. `git add / commit / push` para o GitHub.
2. O workflow [`.github/workflows/build.yml`](../.github/workflows/build.yml) roda em `windows-latest`:
   - `dotnet restore` + `dotnet build Piloto.sln` (compila os **10 projetos**),
   - `dotnet test tests/Piloto.Tests` (testes de lógica pura),
   - `dotnet publish` self-contained + Inno Setup → `PilotoSetup-0.1.0.exe` como artifact.
3. Na máquina de teste (com áudio/headset e modelos): instalar o `Setup.exe` e executar.

> Se quiser compilar/rodar localmente, instale o **.NET 8 SDK** e use
> `scripts/build-installer.ps1` ou `dotnet run --project src/Piloto.App`.

## Implementado

| Camada | Projeto | Situação |
|---|---|---|
| Cliente do servidor de transcrição (contrato 2.0, long-poll, idempotência, classificação de erro) | `Piloto.Remote` | ✅ implementado⁴ |
| Domínio, config, normalização, grounding, pipeline, fila | `Piloto.Core` | ✅ completo |
| Regras (telefone, CPF+dígito verificador, e-mail, data, valor, protocolo, confiança) | `Piloto.Rules` | ✅ completo |
| Fusão do contato do cadastro do Zendesk com os campos objetivos | `Piloto.Core` (`ContactMerger`) | ✅ completo |
| Leitura de e-mail/telefone/nome do solicitante no DOM | `extension/` | ⚠️ seletores placeholder³ — o caminho `mailto:`/`tel:` funciona sem eles |
| SQLite + FTS5, migrações, exportação TXT/JSON/CSV, PII, retenção | `Piloto.Data` | ✅ completo |
| WebSocket local da extensão (TcpListener, sem ACL de admin) | `Piloto.Bridge` | ✅ completo |
| Gravador WASAPI 2 canais → 16 kHz mono | `Piloto.Audio` | ✅ implementado¹ |
| Transcrição Whisper local (task=transcribe, pt, fusão por timestamp) | `Piloto.Transcription` | 🗄️ preservado fora do DI⁵ |
| Resumo LLM (LLamaSharp, gramática GBNF, listas fechadas, temp 0) | `Piloto.Llm` | ✅ implementado² |
| App WPF (bandeja, histórico, busca, detalhe, configurações, gravação) | `Piloto.App` | ✅ completo |
| Extensão MV3 (content script + service worker) | `extension/` | ✅ esqueleto³ |
| Testes xUnit (normalização, regras, grounding, export, repositório) | `tests/Piloto.Tests` | ✅ completo |
| Instalador Inno Setup + CI | `installer/`, `.github/` | ✅ completo |

¹ Depende de hardware de áudio real e dos modelos — só executa de fato na máquina de teste.
² **Ponto a validar no CI.** LLamaSharp muda a API entre versões; o código usa `LLamaSharp 0.20.0`
   (necessária para carregar Gemma 3) e apenas a superfície estável de sampling
   (`DefaultSamplingPipeline.Temperature`, `StatelessExecutor.InferAsync`). A **gramática GBNF** já está
   pronta em [`GbnfGrammarBuilder.cs`](../src/Piloto.Llm/GbnfGrammarBuilder.cs), mas fica **desligada por
   padrão** para não acoplar o build à API de gramática (a mais volátil entre versões). Isso não afeta a
   corretude: o **grounding** (camada 3) valida as listas fechadas e anula valores fora delas. Para
   reativar a saída forçada por gramática, siga o comentário em
   [`LlamaSummaryExtractor.cs`](../src/Piloto.Llm/LlamaSummaryExtractor.cs).
³ Os seletores do DOM do Zendesk em [`content-zendesk.js`](../extension/content-zendesk.js) são
   **placeholders**: precisam ser ajustados inspecionando a página real do atendente (F4/F5 do roadmap).
⁴ **Testável sem GPU.** O servidor tem o modo falso (`.\executar.ps1 -Falso`) que implementa o
   contrato 2.0 por inteiro; `MapeadorContratoTests` roda contra JSON do contrato no CI, sem rede.
   O que ainda não foi exercido é a rede real: upload de ligação longa, queda no meio do envio e
   reenvio idempotente depois de reiniciar o app.
⁵ **Fora do contêiner desde a migração** — o `RemoteTranscriber` ocupa o lugar no DI. O arquivo
   fica no repositório porque os limiares, o padrão de alucinação e a compressão de timestamps
   dele foram calibrados contra ligações reais; essa memória é cara de reconstruir. Continua
   compilando (o CI garante) e não depende mais de nada que a migração removeu.

## Regras de ouro respeitadas

- Campo não encontrado = `null` / "Não identificado" (nunca inventar) — garantido pelo grounding.
- `motivo_contato`, `produto`, `status` são listas fechadas — a gramática GBNF restringe e o grounding valida.
- Transcrição sempre `task=transcribe` + `language=pt` (invariante do contrato do servidor).
- Uma ligação por vez, fila persistida em SQLite. **Servidor fora do ar não descarta nada**:
  falha de rede não consome tentativa, o item recua e é reenviado com a mesma chave.
- Processamento dentro da operação (o áudio agora sai da máquina, mas não da rede interna);
  indicador de gravação na bandeja + "não gravar esta chamada"; PII mascarada na exportação.

## Próximos passos sugeridos

1. Rodar o CI e confirmar build verde dos 10 projetos (com atenção a `Piloto.Llm`).
2. **Ponta a ponta contra o servidor real**, na ordem que mais ensina: (a) uma ligação curta;
   (b) uma ligação com o servidor desligado, para ver a fila segurar e reenviar; (c) fechar o
   app no meio do processamento e reabrir, para ver a idempotência reaproveitar o job;
   (d) uma ligação longa (>30 min), que é onde o upload e o timeout aparecem.
3. Baixar o modelo de resumo na máquina de teste (`scripts/download-models.ps1`) e validar o
   pipeline completo.
4. Ajustar os seletores reais do Zendesk na extensão (fases F4/F5 do roadmap).
5. Avaliar loopback **por processo** (hoje é loopback do dispositivo — exige headset, como o README já obriga).
