#requires -Version 5.1
<#
.SYNOPSIS
    Publish self-contained + geração do instalador Inno Setup (opcional, local).
.DESCRIPTION
    O caminho normal é deixar o GitHub Actions gerar o Setup.exe. Este script existe
    para quem quiser reproduzir o instalador localmente. Requer .NET 8 SDK e Inno Setup 6.
#>
[CmdletBinding()]
param(
    [string]$Configuracao = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot

function Write-Passo($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

Write-Passo 'dotnet restore'
dotnet restore (Join-Path $raiz 'Piloto.sln')

Write-Passo 'dotnet publish (self-contained)'
dotnet publish (Join-Path $raiz 'src\Piloto.App') `
    -c $Configuracao -r $Runtime --self-contained true `
    -p:PublishSingleFile=false `
    -o (Join-Path $raiz 'publish')

# Localiza iscc.exe
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw 'ISCC.exe (Inno Setup 6) não encontrado. Instale em https://jrsoftware.org/isinfo.php'
}

Write-Passo "Inno Setup: $iscc"
& $iscc (Join-Path $raiz 'installer\setup.iss')

Write-Passo 'Instalador gerado em installer\Output\'
Get-ChildItem (Join-Path $raiz 'installer\Output') -Filter 'PilotoSetup-*.exe' -ErrorAction SilentlyContinue |
    Format-Table Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}} -AutoSize
