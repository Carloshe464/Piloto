# Click Write — Transcrição Local de Ligações (Zendesk)

Aplicativo Windows que grava as ligações atendidas pelo discador web do Zendesk, transcreve localmente com Whisper e gera registros estruturados (resumo, motivo, pedido, campos objetivos) — **sem enviar áudio ou texto para nenhuma API externa**.

> **Status:** 1.0. Hardware de referência: Intel Core i5 10ª geração, 12 GB RAM, SSD, Windows 10/11 64 bits.

> **Sobre o nome:** o produto se chama **Click Write** a partir da 1.0 (era "Piloto" até a 0.7.x).
> A mudança é de identidade — instalador, executável (`ClickWrite.exe`), pasta de instalação e
> telas. **Não** foram renomeados, de propósito:
>
> | O quê | Por quê |
> |---|---|
> | Projetos e namespaces `Piloto.*` | Renomear seria churn no repositório inteiro sem efeito nenhum para quem usa o app |
> | Pasta de dados `%LOCALAPPDATA%\Piloto` | Guarda banco, histórico e modelos (~2,6 GB) — renomear custaria um novo download por máquina |
> | Mutex `PilotoAppMutex` | É por ele que o instalador da 1.0 detecta uma 0.7.x rodando e a fecha antes de atualizar |

---

## Como funciona

```text
Atendente clica em "Gravar" (MVP) — automático via extensão na fase 2
        ↓
Gravador WASAPI captura 2 canais:
  • microfone (atendente)  • loopback do navegador (cliente/Zendesk)
        ↓
Extensão Chrome/Edge lê o DOM do Zendesk → número, ticket, status
  e envia ao app via WebSocket local (127.0.0.1)
        ↓
Chamada encerrada → par de arquivos entra na fila (1 por vez, SQLite)
        ↓
Whisper (small, quantizado) transcreve cada canal — task=transcribe, language=pt
        ↓
Fusão por timestamp → diálogo rotulado [Atendente]/[Cliente]
        ↓
Normalização do texto (números falados → dígitos, etc.)
        ↓
Camada 1 — REGRAS: telefone, CPF, e-mail, datas, valores, protocolo (+ confiança)
        ↓
Fusão com o CADASTRO: e-mail, telefone e número do discador lidos do Zendesk
  entram como fonte confiável e vencem o mesmo valor ouvido na ligação
        ↓
Camada 2 — LLM LOCAL (Gemma 3 4B Q4 via llama.cpp): resumo PT-BR,
  motivo/produto/status (listas fechadas), pedido, próximo passo
  — saída JSON forçada por gramática, temperatura 0
        ↓
Camada 3 — GROUNDING: valor que não existe na transcrição vira null
  e marca o registro para revisão humana
        ↓
SQLite + FTS5 → histórico, busca, exportação TXT/JSON/CSV, notificação
```

Regras de ouro do pipeline:

- Campo não encontrado = `null` / `Não identificado`. **Nunca inventar dados.**
- **Cadastro vence transcrição.** E-mail e telefone ditados por voz são o que o Whisper mais
  erra, e o erro sai plausível — nenhuma regra detecta um dígito trocado. Quando a extensão
  lê o contato do solicitante no Zendesk, esse valor substitui o ouvido e é marcado como
  `Cadastro` na tela; o que foi ouvido continua visível com a confiança da detecção.
- `motivo_contato`, `produto` e `status` são **listas fechadas** configuráveis pelo administrador — o LLM escolhe, não redige.
- Whisper sempre em `task=transcribe` + `language=pt` (o modo `translate` verte para inglês — nunca usar).
- Uma transcrição por vez, processo em prioridade baixa: a máquina do atendente continua usável.

---

## Stack

| Componente | Tecnologia | Observação |
|---|---|---|
| App desktop (UI, bandeja, notificações) | .NET 8 + WPF | Stack única .NET de ponta a ponta |
| Captura de áudio | NAudio (WASAPI mic + loopback por processo) | Requer Windows 10 2004+ |
| Transcrição | Whisper.net (bindings do whisper.cpp) | Modelo `ggml-small` quantizado |
| Resumo / campos interpretativos | LLamaSharp (bindings do llama.cpp) | Gemma 3 4B instruct Q4_K_M (GGUF) |
| Extensão do navegador | Chrome/Edge Manifest V3, JS puro | Lê DOM do Zendesk, fala com o app via WebSocket local |
| Banco de dados | SQLite + FTS5 (`unicode61 remove_diacritics 2`) | Fila persistida + busca full-text |
| Instalador | Inno Setup 6 | `Setup.exe` com assistente, publish self-contained (não exige .NET na máquina destino) |
| CI/CD | GitHub Actions (runner `windows-latest`) | Build, testes e instalador gerados na nuvem |

---

## Estrutura do repositório

```text
Piloto/                         # nome do repositório; o produto é o Click Write
├── src/
│   ├── Piloto.App/             # WPF: bandeja, histórico, detalhe, configurações
│   ├── Piloto.Core/            # domínio: fila, pipeline, normalização, grounding
│   ├── Piloto.Audio/           # gravador WASAPI 2 canais (NAudio)
│   ├── Piloto.Transcription/   # Whisper.net (transcrição + fusão de canais)
│   ├── Piloto.Llm/             # LLamaSharp: prompts, gramática JSON, listas fechadas
│   ├── Piloto.Rules/           # regex, dicionários e confiança dos campos objetivos
│   ├── Piloto.Data/            # SQLite, FTS5, migrações, exportação TXT/JSON/CSV
│   └── Piloto.Bridge/          # WebSocket 127.0.0.1 para a extensão
├── extension/                  # extensão MV3 (content script do Zendesk + bridge)
├── tests/
│   └── Piloto.Tests/           # xUnit — rodam no CI, não nesta máquina
├── installer/
│   └── setup.iss               # script Inno Setup
├── scripts/
│   ├── download-models.ps1     # baixa os modelos GGUF/GGML (não versionados)
│   └── build-installer.ps1     # publish + iscc local (opcional)
├── config/
│   ├── appsettings.json        # caminhos, modelo, retenção, porta do bridge
│   ├── listas.json             # listas fechadas: motivo, produto, status
│   └── glossario.txt           # initial_prompt do Whisper (nomes de produtos, jargão)
├── .github/workflows/build.yml # CI: build → testes → instalador → artifact/release
└── README.md
```

> **Modelos não são versionados no Git** (são grandes: Whisper small ~500 MB, Gemma 3 4B Q4 ~2,5 GB). Eles são baixados pelo `scripts/download-models.ps1` ou pelo próprio app na primeira execução, e ficam em `%LOCALAPPDATA%\Piloto\models\`.

---

## Ambiente de desenvolvimento (esta máquina)

Aqui você **edita e versiona**; o build/testes/instalador rodam no GitHub Actions.

### Pré-requisitos

1. **Git** — <https://git-scm.com>
2. **.NET 8 SDK (LTS)** — <https://dotnet.microsoft.com/download/dotnet/8.0>
3. **Editor:** Visual Studio 2022 Community (workload ".NET Desktop") **ou** VS Code + extensão *C# Dev Kit*
4. *(Opcional, só se quiser gerar instalador localmente)* **Inno Setup 6** — <https://jrsoftware.org/isinfo.php>

Verificação rápida:

```powershell
git --version
dotnet --version   # deve exibir 8.x
```

### Clonar e compilar

```powershell
git clone https://github.com/<SEU_USUARIO>/piloto.git
cd piloto
dotnet restore
dotnet build                      # compila tudo, sem executar
```

### Rodar o app em modo desenvolvimento

```powershell
dotnet run --project src/Piloto.App
```

Sem os modelos baixados o app abre normalmente, mas a fila fica pausada com o aviso "modelos ausentes". Para testar o pipeline completo nesta máquina (não obrigatório):

```powershell
.\scripts\download-models.ps1     # baixa Whisper small + Gemma 3 4B para %LOCALAPPDATA%\Piloto\models
```

### Desenvolver a extensão

1. Abra `chrome://extensions` (ou `edge://extensions`), ative o **Modo do desenvolvedor**.
2. **Carregar sem compactação** → aponte para a pasta `extension/`.
3. A extensão só ativa em `*.zendesk.com` e conversa com o app em `ws://127.0.0.1:8517` (porta configurável em `config/appsettings.json`).
4. Clique no ícone da extensão: o popup mostra **"Lido do Zendesk agora"** com o que os
   seletores estão capturando naquele instante. Campo em `—` com um ticket aberto = seletor
   desatualizado; é por aí que se ajusta `SELETORES` em `content-zendesk.js`.

### Onde mexer para modificar cada coisa

| Quero alterar... | Arquivo/projeto |
|---|---|
| Listas fechadas (motivo, produto, status) | `config/listas.json` |
| Regex e dicionários dos campos objetivos | `src/Piloto.Rules/` |
| Prompt do resumo e schema JSON do LLM | `src/Piloto.Llm/Prompts/` |
| Glossário que melhora o reconhecimento do Whisper | `config/glossario.txt` |
| Modelo usado (base/small; Gemma/Llama) e threads | `config/appsettings.json` |
| Seletores do DOM do Zendesk (inclui e-mail/telefone do cliente) | `extension/content-zendesk.js` → `SELETORES` |
| Template do TXT exportado | `src/Piloto.Data/Export/` |
| Retenção/exclusão automática de áudios | `config/appsettings.json` → `retencaoDias` |

---

## Fluxo de trabalho: editar aqui → GitHub → executar lá

Esta máquina **não roda os testes nem o piloto**. O ciclo é:

```text
1. Editar código nesta máquina
2. git add / commit / push  →  GitHub
3. GitHub Actions compila, roda os testes e gera o Setup.exe
4. Na máquina de teste: baixar o Setup.exe (aba Actions → artifact, ou Releases)
5. Instalar (assistente next→next→finish) e executar o piloto
```

### CI — `.github/workflows/build.yml`

O workflow deve fazer, em um runner `windows-latest`:

```yaml
name: build
on:
  push:
    branches: [main]
    tags: ["v*"]
  pull_request:

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: "8.0.x" }

      - name: Testes
        run: dotnet test tests/Piloto.Tests -c Release --logger trx

      - name: Publish self-contained
        run: >
          dotnet publish src/Piloto.App -c Release -r win-x64
          --self-contained true -p:PublishSingleFile=false
          -o publish/

      - name: Instalador (Inno Setup)
        run: |
          choco install innosetup -y --no-progress
          iscc installer/setup.iss

      - name: Artifact
        uses: actions/upload-artifact@v4
        with:
          name: clickwrite-setup
          path: installer/Output/ClickWriteSetup-*.exe

      - name: Release (somente em tag v*)
        if: startsWith(github.ref, 'refs/tags/v')
        uses: softprops/action-gh-release@v2
        with:
          files: installer/Output/ClickWriteSetup-*.exe
```

### Publicar uma versão instalável

```powershell
git tag v0.1.0
git push origin v0.1.0
```

O instalador aparece em **Releases** do repositório, pronto para baixar na máquina de teste.

---

## Instalação na máquina de teste/produção

1. Baixar `ClickWriteSetup-x.y.z.exe` da página de **Releases**.
2. Executar — assistente padrão do Windows (pasta, atalhos, iniciar com o Windows).

> **Atualizando uma máquina que tem a 0.7.x ("Piloto")?** Basta rodar o instalador da 1.0
> por cima: o app em execução é fechado (com confirmação), a instalação vai para
> `Arquivos de Programas\Click Write`, e a pasta antiga, o grupo do menu Iniciar, o atalho
> da área de trabalho e a entrada de inicialização da 0.7.x são removidos. Fica **uma**
> entrada em "Adicionar ou remover programas". **Nenhum dado é perdido** — banco, histórico
> e modelos continuam em `%LOCALAPPDATA%\Piloto`, que não é tocado.

3. Na primeira execução o app oferece **baixar os modelos** (~3 GB, uma única vez) ou aponta para uma pasta com os modelos já copiados (ambiente sem internet).
4. Instalar a extensão no navegador do atendente (pasta `extension` incluída na instalação; em escala, distribuir via política de grupo/GPO).
5. Configurar na tela administrativa: listas fechadas, glossário, retenção e porta do bridge.

Requisitos da máquina destino: Windows 10 2004+ 64 bits, 12 GB RAM recomendado (8 GB opera com Whisper `base` e camada LLM desligada), SSD com ~10 GB livres, **headset** (obrigatório — caixas de som contaminam os canais) e Zendesk aberto em **janela dedicada do navegador**.

O publish é *self-contained*: a máquina destino **não precisa ter .NET instalado**.

> **SmartScreen:** sem assinatura de código, o Windows exibirá aviso "editor desconhecido" na instalação. Para o piloto interno, clicar em "Mais informações → Executar assim mesmo" resolve; para distribuição ampla, planejar certificado de assinatura de código.

---

## Configuração (`config/appsettings.json`)

```json
{
  "bridge":        { "porta": 8517 },
  "audio":         { "processoNavegador": "chrome", "formato": "flac", "taxaHz": 16000 },
  "whisper":       { "modelo": "ggml-small-q5_1.bin", "idioma": "pt", "threads": 5 },
  "llm":           { "habilitado": true, "modelo": "gemma-3-4b-it-Q4_K_M.gguf", "temperatura": 0 },
  "fila":          { "simultaneas": 1, "prioridadeProcesso": "BelowNormal" },
  "retencaoDias":  { "audio": 30, "transcricao": 180 },
  "pastaDados":    "%LOCALAPPDATA%\\Piloto"
}
```

---

## Privacidade e conformidade (LGPD)

- Processamento 100% local — nenhum áudio, transcrição ou metadado sai da máquina.
- O app é o **gravador**: indicador visível de gravação na bandeja + botão "não gravar esta chamada".
- Retenção e exclusão automáticas configuráveis; exportações com PII mascarada por padrão.
- Recomendado **BitLocker** ativo nas máquinas do piloto (dados em repouso).
- Base legal e aviso aos interlocutores conforme orientação jurídica interna.

---

## Roadmap do piloto

- [ ] **F0 — Prova técnica:** gravar 30–50 ligações reais, medir WER do Whisper `base` vs `small` e escolher o modelo do resumo (Gemma 3 4B vs Qwen3 4B) em teste cego.
- [ ] **F1 — Gravador:** WASAPI 2 canais, botão manual, fila SQLite, sobrevive a troca de headset/queda do app.
- [ ] **F2 — Pipeline:** Whisper.net → fusão → normalização → regras → LLM → grounding → exportação.
- [ ] **F3 — App:** histórico, busca FTS5, tela de detalhe, notificações, contadores (nº de chamadas, tempo total falado).
- [ ] **F4 — Extensão:** metadados do DOM do Zendesk via WebSocket; preparar eventos p/ gravação automática.
- [ ] **F5 — Instalador + CI:** Inno Setup, GitHub Actions, release v0.1.0.
- [ ] **F6 — Piloto controlado:** poucos atendentes, revisão humana, métricas (fila, tempo de transcrição, % campos corretos, resumo fiel/completo/útil ≥ 90/80%).

### Fora do escopo do piloto

Transcrição em tempo real, múltiplas transcrições simultâneas, diarização por IA (desnecessária — os 2 canais já separam os interlocutores), captura SIP/RTP, integração CRM/ERP, operação multi-filial.

---

## Licenças de terceiros

Whisper (MIT), whisper.cpp (MIT), llama.cpp (MIT), NAudio (MIT), LLamaSharp (MIT), Whisper.net (MIT), SQLite (domínio público), Gemma 3 (Gemma Terms of Use — verificar termos para uso comercial interno), Inno Setup (livre para distribuição).
