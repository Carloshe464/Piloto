#requires -Version 5.1
<#
.SYNOPSIS
    Baixa os modelos GGML (Whisper) e GGUF (LLM) para %LOCALAPPDATA%\Piloto\models.
.DESCRIPTION
    Os modelos NÃO são versionados no Git (são grandes). Rode este script uma vez
    na máquina onde o pipeline completo será testado. Em ambiente sem internet,
    copie os arquivos manualmente para a pasta de destino exibida no final.
#>
[CmdletBinding()]
param(
    [string]$Destino = (Join-Path $env:LOCALAPPDATA 'Piloto\models'),
    [ValidateSet('small', 'base')]
    [string]$Whisper = 'small',
    # 4b: qualidade padrão (~2,4 GB; exige ~8 GB de RAM). 1b: máquinas com 4 GB (~0,8 GB).
    # Ao usar 1b, ajuste "llm.modelo" no config para gemma-3-1b-it-Q4_K_M.gguf.
    [ValidateSet('4b', '1b')]
    [string]$Llm = '4b',
    [switch]$SemLlm
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # acelera Invoke-WebRequest

function Write-Passo($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# Fontes oficiais (Hugging Face). Ajuste os quantizados conforme o config.
$modelos = @{
    'small' = @{
        Nome = 'ggml-small-q5_1.bin'
        Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin'
    }
    'base' = @{
        Nome = 'ggml-base-q5_1.bin'
        Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin'
    }
}

$llms = @{
    '4b' = @{
        Nome = 'gemma-3-4b-it-Q4_K_M.gguf'
        Url  = 'https://huggingface.co/unsloth/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf'
    }
    '1b' = @{
        Nome = 'gemma-3-1b-it-Q4_K_M.gguf'
        Url  = 'https://huggingface.co/unsloth/gemma-3-1b-it-GGUF/resolve/main/gemma-3-1b-it-Q4_K_M.gguf'
    }
}

if (-not (Test-Path $Destino)) {
    Write-Passo "Criando pasta de modelos: $Destino"
    New-Item -ItemType Directory -Force -Path $Destino | Out-Null
}

function Get-Modelo($nome, $url) {
    $alvo = Join-Path $Destino $nome
    if (Test-Path $alvo) {
        Write-Passo "Já existe, pulando: $nome"
        return
    }
    Write-Passo "Baixando $nome ..."

    # Baixa em arquivo temporário e renomeia no fim: um download interrompido nunca
    # deixa um modelo truncado com o nome final (que seria "pulado" na próxima execução).
    $temp = "$alvo.baixando"
    if (Test-Path $temp) { Remove-Item $temp -Force }

    # Invoke-WebRequest no Windows PowerShell 5.1 carrega o arquivo INTEIRO em memória
    # (estoura com modelos de GBs). curl.exe (nativo do Win10 1803+) faz streaming.
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source -L --fail --retry 3 --retry-delay 5 -o $temp $url
        if ($LASTEXITCODE -ne 0) {
            if (Test-Path $temp) { Remove-Item $temp -Force }
            throw "Falha ao baixar $nome (curl saiu com código $LASTEXITCODE)."
        }
    }
    else {
        # Fallback: WebClient também faz streaming direto para o disco.
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        (New-Object Net.WebClient).DownloadFile($url, $temp)
    }

    Move-Item $temp $alvo -Force
    $mb = [math]::Round((Get-Item $alvo).Length / 1MB, 1)
    Write-Host "    OK ($mb MB)" -ForegroundColor Green
}

$w = $modelos[$Whisper]
Get-Modelo $w.Nome $w.Url

if (-not $SemLlm) {
    $l = $llms[$Llm]
    Get-Modelo $l.Nome $l.Url
}
else {
    Write-Passo 'LLM ignorado (-SemLlm).'
}

Write-Host ''
Write-Passo "Modelos em: $Destino"
Get-ChildItem $Destino | Format-Table Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}} -AutoSize
