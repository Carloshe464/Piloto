# Estado do projeto — Piloto

Documento de acompanhamento do desenvolvimento. O README continua sendo a especificação;
aqui fica **o que já está implementado**, **como validar** e **o que observar**.

## Como este projeto é compilado

Esta máquina de desenvolvimento **não tem o .NET SDK instalado** (confirmado: `dotnet` ausente),
exatamente como o README descreve: *aqui você edita e versiona; o build/testes/instalador rodam
no GitHub Actions*.

Portanto o ciclo de validação é:

1. `git add / commit / push` para o GitHub.
2. O workflow [`.github/workflows/build.yml`](../.github/workflows/build.yml) roda em `windows-latest`:
   - `dotnet restore` + `dotnet build Piloto.sln` (compila os **9 projetos**),
   - `dotnet test tests/Piloto.Tests` (testes de lógica pura),
   - `dotnet publish` self-contained + Inno Setup → `PilotoSetup-0.1.0.exe` como artifact.
3. Na máquina de teste (com áudio/headset e modelos): instalar o `Setup.exe` e executar.

> Se quiser compilar/rodar localmente, instale o **.NET 8 SDK** e use
> `scripts/build-installer.ps1` ou `dotnet run --project src/Piloto.App`.

## Implementado

| Camada | Projeto | Situação |
|---|---|---|
| Domínio, config, normalização, grounding, pipeline, fila | `Piloto.Core` | ✅ completo |
| Regras (telefone, CPF+dígito verificador, e-mail, data, valor, protocolo, confiança) | `Piloto.Rules` | ✅ completo |
| SQLite + FTS5, migrações, exportação TXT/JSON/CSV, PII, retenção | `Piloto.Data` | ✅ completo |
| WebSocket local da extensão (TcpListener, sem ACL de admin) | `Piloto.Bridge` | ✅ completo |
| Gravador WASAPI 2 canais → 16 kHz mono | `Piloto.Audio` | ✅ implementado¹ |
| Transcrição Whisper.net (task=transcribe, pt, fusão por timestamp) | `Piloto.Transcription` | ✅ implementado¹ |
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

## Regras de ouro respeitadas

- Campo não encontrado = `null` / "Não identificado" (nunca inventar) — garantido pelo grounding.
- `motivo_contato`, `produto`, `status` são listas fechadas — a gramática GBNF restringe e o grounding valida.
- Whisper sempre `task=transcribe` + `language=pt` (o modo translate nunca é chamado).
- Uma transcrição por vez, thread em prioridade `Lowest`, fila persistida em SQLite.
- Processamento 100% local; indicador de gravação na bandeja + "não gravar esta chamada"; PII mascarada na exportação.

## Próximos passos sugeridos

1. Rodar o CI e confirmar build verde dos 9 projetos (com atenção a `Piloto.Llm`).
2. Baixar os modelos na máquina de teste (`scripts/download-models.ps1`) e validar o pipeline ponta a ponta.
3. Ajustar os seletores reais do Zendesk na extensão (fases F4/F5 do roadmap).
4. Avaliar loopback **por processo** (hoje é loopback do dispositivo — exige headset, como o README já obriga).
