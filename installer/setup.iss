; Inno Setup 6 — instalador do Piloto (publish self-contained; não exige .NET na máquina destino).
; Gera installer\Output\PilotoSetup-<versao>.exe. Rode via scripts\build-installer.ps1 ou no CI.

#define MyAppName "Piloto"
#define MyAppVersion "0.4.0"
#define MyAppPublisher "Piloto"
#define MyAppExeName "Piloto.exe"
; Mesmo nome criado por App.xaml.cs — é como o setup detecta o app em execução.
#define MyAppMutex "PilotoAppMutex"

[Setup]
AppId={{9C2B4E7A-1F3D-4B8E-9A6C-0C1D2E3F4A5B}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=PilotoSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
; Atualização in-place: mesmo AppId instala por cima, preservando os dados do usuário
; (%LOCALAPPDATA%\Piloto). O AppMutex é a rede de segurança caso o app reabra depois
; do fechamento automático feito em [Code].
AppMutex={#MyAppMutex}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na área de trabalho"; GroupDescription: "Atalhos adicionais:"
Name: "startupicon"; Description: "Iniciar o Piloto com o Windows"; GroupDescription: "Inicialização:"

[Files]
; Saída do 'dotnet publish -c Release -r win-x64 --self-contained true -o publish/'
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Extensão do navegador (para carregar sem compactação ou distribuir por GPO)
Source: "..\extension\*"; DestDir: "{app}\extension"; Flags: recursesubdirs createallsubdirs ignoreversion
; Script de download dos modelos (para rodar direto na máquina de teste)
Source: "..\scripts\download-models.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Baixar modelos (Whisper + Gemma)"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoExit -ExecutionPolicy Bypass -NoProfile -File ""{app}\scripts\download-models.ps1"""; \
  WorkingDir: "{app}\scripts"; \
  Comment: "Baixa os modelos para %LOCALAPPDATA%\Piloto\models"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "Piloto"; ValueData: """{app}\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: startupicon

[Run]
; Entradas postinstall viram checkboxes na última tela do assistente e rodam como o
; usuário original (não elevado) — os modelos caem no %LOCALAPPDATA% do usuário certo.
; Os modelos (~2,6 GB) não são embutidos no setup: o GitHub Releases limita artefatos
; a 2 GB e cada atualização reenviaria tudo; em máquinas sem internet, use o atalho
; "Baixar modelos" do menu Iniciar em outra máquina e copie a pasta manualmente.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoExit -ExecutionPolicy Bypass -NoProfile -File ""{app}\scripts\download-models.ps1"""; \
  WorkingDir: "{app}\scripts"; \
  Description: "Baixar os modelos de IA agora (~2,6 GB — requer internet)"; \
  Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Description: "Executar o {#MyAppName} agora"; \
  Flags: nowait postinstall skipifsilent

[Code]
{ Fecha o Piloto automaticamente antes de atualizar/desinstalar, com confirmação.
  Sem isso, o app na bandeja (que só se esconde ao fechar a janela) travaria os
  arquivos e a atualização falharia com "arquivo em uso". }
function FecharPilotoSeNecessario(): Boolean;
var
  Codigo: Integer;
begin
  Result := True;
  if not CheckForMutexes('{#MyAppMutex}') then
    Exit;

  if MsgBox('O {#MyAppName} está em execução e será fechado para continuar.' + #13#10 +
            'Se houver uma gravação em andamento, ela será perdida. Continuar?',
            mbConfirmation, MB_YESNO) = IDNO then
  begin
    Result := False;
    Exit;
  end;

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyAppExeName} /F', '',
       SW_HIDE, ewWaitUntilTerminated, Codigo);
  Sleep(800);
end;

function InitializeSetup(): Boolean;
begin
  Result := FecharPilotoSeNecessario();
end;

function InitializeUninstall(): Boolean;
begin
  Result := FecharPilotoSeNecessario();
end;
