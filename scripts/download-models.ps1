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
    # auto: decide pela RAM (>= 7 GB -> turbo [large-v3-turbo, o melhor local]; menos -> small).
    # O app usa em runtime o maior modelo presente que couber na memória.
    [ValidateSet('auto', 'small', 'base', 'medium', 'turbo')]
    [string]$Whisper = 'auto',
    # auto: decide pela RAM da máquina (>= 7 GB -> 4b; menos -> 1b). O app usa o que couber
    # na memória em runtime, então não é preciso ajustar o config ao baixar o 1b.
    # 4b: qualidade padrão (~2,4 GB; exige ~8 GB de RAM). 1b: máquinas com 4 GB (~0,8 GB).
    [ValidateSet('auto', '4b', '1b')]
    [string]$Llm = 'auto',
    [switch]$SemLlm,
    # Pula a remoção de modelos obsoletos de versões anteriores.
    [switch]$SemLimpeza
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
    'medium' = @{
        Nome = 'ggml-medium-q5_0.bin'
        Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium-q5_0.bin'
    }
    # large-v3-turbo quantizado: qualidade de large com arquivo de ~570 MB — o teto local.
    'turbo' = @{
        Nome = 'ggml-large-v3-turbo-q5_0.bin'
        Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin'
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

    Move-Item $temp $alvo -Force
    $mb = [math]::Round((Get-Item $alvo).Length / 1MB, 1)
    Write-Host "    OK ($mb MB)" -ForegroundColor Green
}

$ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)

# A RAM total engana: quem decide em runtime é a memória LIVRE com Chrome+Zendesk
# abertos — nas máquinas da operação (12 GB) sobra ~1,3 GB e o Gemma 4B (~3,1 GB para
# carregar) nunca cabe. Por isso o 'auto' baixa TAMBÉM o modelo pequeno de cada tipo:
# é a rede de segurança para onde o app cai quando o grande não couber naquele momento.
if ($Whisper -eq 'auto') {
    $listaWhisper = if ($ramGb -ge 7) { @('turbo', 'small') } else { @('small') }
    Write-Passo "RAM total: $ramGb GB -> Whisper $($listaWhisper -join ' + ')"
}
else { $listaWhisper = @($Whisper) }
foreach ($chave in $listaWhisper) {
    $m = $modelos[$chave]
    Get-Modelo $m.Nome $m.Url
}

if (-not $SemLlm) {
    if ($Llm -eq 'auto') {
        $listaLlm = if ($ramGb -ge 7) { @('4b', '1b') } else { @('1b') }
        Write-Passo "RAM total: $ramGb GB -> LLM $($listaLlm -join ' + ')"
    }
    else { $listaLlm = @($Llm) }
    foreach ($chave in $listaLlm) {
        $m = $llms[$chave]
        Get-Modelo $m.Nome $m.Url
    }
}
else {
    Write-Passo 'LLM ignorado (-SemLlm).'
}

# --- limpeza de modelos obsoletos -------------------------------------------
# Atualização em cima de atualização acumula modelos de versões antigas: o uninstall
# não toca na pasta de modelos (por desenho — são gigas baixados). Não é só disco:
# o app escolhe modelo por tamanho ("maior = melhor"), então um legado maior que o
# atual ganha a preferência e ocupa mais RAM à toa (ex.: medium de 539 MB competindo
# com o turbo). Remove APENAS nomes que alguma versão deste script já baixou — arquivo
# desconhecido (modelo colocado manualmente) é preservado com aviso. Roda depois dos
# downloads (nunca deixa a máquina sem modelo se um download falhar) e só no modo
# 'auto', em que o conjunto desejado é o curado; escolha explícita não apaga nada.
if (-not $SemLimpeza -and $Whisper -eq 'auto' -and ($SemLlm -or $Llm -eq 'auto')) {
    $conhecidos = @($modelos.Values | ForEach-Object { $_.Nome })
    if (-not $SemLlm) { $conhecidos += @($llms.Values | ForEach-Object { $_.Nome }) }

    $desejados = @($listaWhisper | ForEach-Object { $modelos[$_].Nome })
    if (-not $SemLlm) { $desejados += @($listaLlm | ForEach-Object { $llms[$_].Nome }) }

    Get-ChildItem $Destino -File | ForEach-Object {
        $nomeBase = $_.Name -replace '\.baixando$', ''
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
