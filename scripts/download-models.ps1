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

$llm = @{
    Nome = 'gemma-3-4b-it-Q4_K_M.gguf'
    Url  = 'https://huggingface.co/unsloth/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf'
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
    Invoke-WebRequest -Uri $url -OutFile $alvo -UseBasicParsing
    $mb = [math]::Round((Get-Item $alvo).Length / 1MB, 1)
    Write-Host "    OK ($mb MB)" -ForegroundColor Green
}

$w = $modelos[$Whisper]
Get-Modelo $w.Nome $w.Url

if (-not $SemLlm) {
    Get-Modelo $llm.Nome $llm.Url
}
else {
    Write-Passo 'LLM ignorado (-SemLlm).'
}

Write-Host ''
Write-Passo "Modelos em: $Destino"
Get-ChildItem $Destino | Format-Table Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}} -AutoSize
