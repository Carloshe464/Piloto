#requires -Version 5.1
<#
.SYNOPSIS
    Acompanha os logs do Click Write em tempo real (fase piloto na máquina de testes).
.DESCRIPTION
    Segue o arquivo de log do dia em %LOCALAPPDATA%\Piloto\logs colorindo por
    severidade (erro em vermelho, aviso em amarelo). Espera o arquivo existir se o
    app ainda não abriu e vira sozinho para o arquivo novo na troca de dia.
    Ctrl+C encerra. Não interfere no app: leitura com compartilhamento de escrita.
#>
[CmdletBinding()]
param(
    [string]$PastaLogs = (Join-Path $env:LOCALAPPDATA 'Piloto\logs'),
    # Quantas linhas já gravadas mostrar antes de começar a seguir ao vivo.
    [int]$Cauda = 50
)

$ErrorActionPreference = 'Stop'
try { $Host.UI.RawUI.WindowTitle = 'Click Write - Logs ao vivo' } catch {}

function ArquivoDoDia { Join-Path $PastaLogs ("piloto-{0:yyyyMMdd}.log" -f (Get-Date)) }

function EscreverLinha([string]$linha) {
    $cor = 'Gray'
    if ($linha -match '\[(Error|Critical)') { $cor = 'Red' }
    elseif ($linha -match '\[Warning') { $cor = 'Yellow' }
    elseif ($linha -match '\[Information') { $cor = 'White' }
    Write-Host $linha -ForegroundColor $cor
}

Write-Host "== Click Write - logs ao vivo ($PastaLogs) - Ctrl+C para sair ==" -ForegroundColor Cyan

while ($true) {
    $arquivo = ArquivoDoDia
    if (-not (Test-Path $arquivo)) {
        Write-Host "Aguardando o app gravar o log de hoje ($(Split-Path $arquivo -Leaf))..." -ForegroundColor DarkGray
        while (-not (Test-Path $arquivo)) {
            Start-Sleep -Seconds 2
            $arquivo = ArquivoDoDia   # cobre a troca de dia durante a espera
        }
    }

    # FileShare ReadWrite: o app continua gravando enquanto lemos.
    $stream = New-Object IO.FileStream($arquivo, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $leitor = New-Object IO.StreamReader($stream)
    try {
        # Cauda: mostra as últimas N linhas já existentes.
        $buffer = New-Object 'System.Collections.Generic.Queue[string]'
        while ($null -ne ($l = $leitor.ReadLine())) {
            $buffer.Enqueue($l)
            if ($buffer.Count -gt $Cauda) { [void]$buffer.Dequeue() }
        }
        foreach ($l in $buffer) { EscreverLinha $l }

        # Ao vivo, até o dia virar.
        while ((ArquivoDoDia) -eq $arquivo) {
            $l = $leitor.ReadLine()
            if ($null -ne $l) { EscreverLinha $l }
            else { Start-Sleep -Milliseconds 300 }
        }
    }
    finally {
        $leitor.Dispose()
        $stream.Dispose()
    }
    Write-Host '== Virada de dia: seguindo o novo arquivo ==' -ForegroundColor Cyan
}
