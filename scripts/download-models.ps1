#requires -Version 5.1
<#
.SYNOPSIS
    Baixa o modelo GGUF (LLM, para o resumo) para %LOCALAPPDATA%\Piloto\models.
.DESCRIPTION
    A TRANSCRIÇÃO NÃO USA MAIS MODELO LOCAL: ela roda no servidor (Whisper com GPU),
    e o app só envia os dois canais de áudio. Este script cuida apenas do modelo de
    resumo, que ainda é local — e remove os modelos Whisper que versões anteriores
    baixaram (são centenas de MB que nunca mais serão usados).

    Os modelos NÃO são versionados no Git (são grandes). Em ambiente sem internet,
    copie o arquivo manualmente para a pasta de destino exibida no final.
#>
[CmdletBinding()]
param(
    [string]$Destino = (Join-Path $env:LOCALAPPDATA 'Piloto\models'),
    # auto: decide pela RAM da máquina (>= 7 GB -> 4b; menos -> 1b). O app usa o que couber
    # na memória em runtime, então não é preciso ajustar o config ao baixar o 1b.
    # 4b: qualidade padrão (~2,4 GB; exige ~8 GB de RAM). 1b: máquinas com 4 GB (~0,8 GB).
    [ValidateSet('auto', '4b', '1b')]
    [string]$Llm = 'auto',
    [switch]$SemLlm,
    # Pula a remoção de modelos obsoletos (inclusive os Whisper que a migração aposentou).
    [switch]$SemLimpeza
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # acelera Invoke-WebRequest

function Write-Passo($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# Fontes oficiais (Hugging Face). Ajuste os quantizados conforme o config.
# Sha256 = lfs.oid oficial no Hugging Face. A retomada de download (curl -C -) em rede
# instável pode montar arquivo corrompido — e GGUF corrompido derruba o app na carga
# nativa, sem erro legível. Todo arquivo é verificado: o existente antes de "pular", o
# baixado antes de valer.

# Modelos Whisper que versões até a 1.0 baixavam. NÃO são mais baixados: a transcrição
# roda no servidor. Os nomes ficam aqui só para a limpeza saber o que remover — sem esta
# lista, os arquivos das máquinas já instaladas virariam "não gerenciados" e ficariam
# ocupando disco para sempre (o uninstall não toca na pasta de modelos, por desenho).
$whisperAposentado = @(
    'ggml-small-q5_1.bin',
    'ggml-base-q5_1.bin',
    'ggml-medium-q5_0.bin',
    'ggml-large-v3-turbo-q5_0.bin'
)

$llms = @{
    '4b' = @{
        Nome = 'gemma-3-4b-it-Q4_K_M.gguf'
        Url  = 'https://huggingface.co/unsloth/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf'
        Sha256 = '04a43a22e8d2003deda5acc262f68ec1005fa76c735a9962a8c77042a74a7d19'
    }
    '1b' = @{
        Nome = 'gemma-3-1b-it-Q4_K_M.gguf'
        Url  = 'https://huggingface.co/unsloth/gemma-3-1b-it-GGUF/resolve/main/gemma-3-1b-it-Q4_K_M.gguf'
        Sha256 = '8270790f3ab69fdfe860b7b64008d9a19986d8df7e407bb018184caa08798ebd'
    }
}

if (-not (Test-Path $Destino)) {
    Write-Passo "Criando pasta de modelos: $Destino"
    New-Item -ItemType Directory -Force -Path $Destino | Out-Null
}

function Test-Integridade($alvo, $sha256) {
    $hash = (Get-FileHash -Algorithm SHA256 -Path $alvo).Hash
    return $hash -eq $sha256.ToUpperInvariant()
}

function Get-Modelo($nome, $url, $sha256) {
    $alvo = Join-Path $Destino $nome
    if (Test-Path $alvo) {
        Write-Passo "Verificando $nome ..."
        if (Test-Integridade $alvo $sha256) {
            Write-Passo "Já existe e está íntegro, pulando: $nome"
            return
        }
        Write-Passo "CORROMPIDO (hash não confere) — apagando e baixando de novo: $nome"
        Remove-Item $alvo -Force
        Remove-Item "$alvo.integridade" -Force -ErrorAction SilentlyContinue
    }
    Write-Passo "Baixando $nome ..."

    # Baixa em arquivo temporário e renomeia no fim: um download interrompido nunca
    # deixa um modelo truncado com o nome final (que seria "pulado" na próxima execução).
    # O parcial NÃO é apagado entre tentativas: com -C - o curl retoma do ponto onde
    # parou — essencial em redes instáveis (queda no meio de 2,3 GB recomeçava do zero).
    $temp = "$alvo.baixando"

    # Invoke-WebRequest no Windows PowerShell 5.1 carrega o arquivo INTEIRO em memória
    # (estoura com modelos de GBs). curl.exe (nativo do Win10 1803+) faz streaming.
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        $maxTentativas = 8
        for ($tentativa = 1; $tentativa -le $maxTentativas; $tentativa++) {
            & $curl.Source -L --fail -C - --retry 3 --retry-delay 5 -o $temp $url
            if ($LASTEXITCODE -eq 0) { break }

            # 33 = servidor recusou retomar deste ponto; recomeça limpo.
            if ($LASTEXITCODE -eq 33 -and (Test-Path $temp)) { Remove-Item $temp -Force }

            if ($tentativa -eq $maxTentativas) {
                throw "Falha ao baixar $nome apos $maxTentativas tentativas (curl saiu com codigo $LASTEXITCODE)."
            }
            Write-Passo "Conexao caiu (curl $LASTEXITCODE) - retomando do ponto onde parou (tentativa $($tentativa + 1)/$maxTentativas)..."
            Start-Sleep -Seconds 10
        }
    }
    else {
        # Fallback: WebClient também faz streaming direto para o disco (sem retomada).
        if (Test-Path $temp) { Remove-Item $temp -Force }
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        (New-Object Net.WebClient).DownloadFile($url, $temp)
    }

    if (-not (Test-Integridade $temp $sha256)) {
        # Parcial montado errado pela retomada: apaga para a próxima execução recomeçar do zero.
        Remove-Item $temp -Force
        throw "Download de $nome veio corrompido (hash nao confere) - rode o script novamente."
    }
    Move-Item $temp $alvo -Force
    $mb = [math]::Round((Get-Item $alvo).Length / 1MB, 1)
    Write-Host "    OK ($mb MB, hash conferido)" -ForegroundColor Green
}

$ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)

# A RAM total engana: quem decide em runtime é a memória LIVRE com Chrome+Zendesk
# abertos — nas máquinas da operação (12 GB) sobra ~1,3 GB e o Gemma 4B (~3,1 GB para
# carregar) nunca cabe. Por isso o 'auto' baixa TAMBÉM o modelo pequeno: é a rede de
# segurança para onde o app cai quando o grande não couber naquele momento.
if (-not $SemLlm) {
    if ($Llm -eq 'auto') {
        $listaLlm = if ($ramGb -ge 7) { @('4b', '1b') } else { @('1b') }
        Write-Passo "RAM total: $ramGb GB -> LLM $($listaLlm -join ' + ')"
    }
    else { $listaLlm = @($Llm) }
    foreach ($chave in $listaLlm) {
        $m = $llms[$chave]
        Get-Modelo $m.Nome $m.Url $m.Sha256
    }
}
else {
    Write-Passo 'LLM ignorado (-SemLlm).'
}

# --- modelos Whisper aposentados pela migração ------------------------------
# Rodam SEMPRE (menos com -SemLimpeza), independentemente das opções de LLM: depois que
# a transcrição foi para o servidor, esses arquivos não têm mais uso nenhum e são as
# centenas de MB mais fáceis de recuperar em máquina de operação.
if (-not $SemLimpeza) {
    foreach ($nome in $whisperAposentado) {
        $alvo = Join-Path $Destino $nome
        if (Test-Path $alvo) {
            $mb = [math]::Round((Get-Item $alvo).Length / 1MB, 1)
            Write-Passo "Removendo modelo de transcrição local (agora no servidor): $nome ($mb MB)"
            Remove-Item $alvo -Force
        }
        Remove-Item "$alvo.baixando" -Force -ErrorAction SilentlyContinue
        Remove-Item "$alvo.integridade" -Force -ErrorAction SilentlyContinue
    }
}

# --- limpeza de modelos obsoletos -------------------------------------------
# Atualização em cima de atualização acumula modelos de versões antigas: o uninstall
# não toca na pasta de modelos (por desenho — são gigas baixados). Não é só disco:
# o app escolhe modelo por tamanho ("maior = melhor"), então um legado maior que o
# atual ganha a preferência e ocupa mais RAM à toa. Remove APENAS nomes que alguma
# versão deste script já baixou — arquivo desconhecido (modelo colocado manualmente) é
# preservado com aviso. Roda depois dos downloads (nunca deixa a máquina sem modelo se
# um download falhar) e só no modo 'auto', em que o conjunto desejado é o curado.
if (-not $SemLimpeza -and ($SemLlm -or $Llm -eq 'auto')) {
    $conhecidos = @($whisperAposentado)
    if (-not $SemLlm) { $conhecidos += @($llms.Values | ForEach-Object { $_.Nome }) }

    $desejados = @()
    if (-not $SemLlm) { $desejados += @($listaLlm | ForEach-Object { $llms[$_].Nome }) }

    Get-ChildItem $Destino -File | ForEach-Object {
        # Parciais (.baixando) e marcadores de verificação (.integridade) seguem o
        # destino do modelo a que pertencem.
        $nomeBase = $_.Name -replace '\.baixando$', '' -replace '\.integridade$', ''
        if ($desejados -contains $nomeBase) { return }
        if ($conhecidos -contains $nomeBase) {
            $mb = [math]::Round($_.Length / 1MB, 1)
            Write-Passo "Removendo modelo obsoleto: $($_.Name) ($mb MB)"
            Remove-Item $_.FullName -Force
        }
        elseif ($_.Extension -in '.bin', '.gguf') {
            Write-Host "    Mantido (nao gerenciado por este script): $($_.Name)" -ForegroundColor Yellow
        }
    }
}

Write-Host ''
Write-Passo "Modelos em: $Destino"
Get-ChildItem $Destino | Format-Table Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}} -AutoSize
