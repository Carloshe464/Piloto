; Inno Setup 6 — instalador do Click Write (publish self-contained; não exige .NET na máquina destino).
; Gera installer\Output\ClickWriteSetup-<versao>.exe. Rode via scripts\build-installer.ps1 ou no CI.

#define MyAppName "Click Write"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Click Write"
#define MyAppExeName "ClickWrite.exe"
; Nome anterior do produto (até a 0.7.x). A 1.0 instala por cima dela e precisa saber
; o que remover: pasta, grupo do menu Iniciar, entrada de inicialização e o executável.
#define NomeAntigo "Piloto"
#define ExeAntigo "Piloto.exe"
; Mesmo nome criado por App.xaml.cs — é como o setup detecta o app em execução.
; NÃO renomeado junto com o produto de propósito: é por este mutex que a 1.0 detecta
; uma 0.7.x rodando e a fecha antes de atualizar. Trocá-lo cegaria o instalador
; justamente na atualização que ele precisa fazer.
#define MyAppMutex "PilotoAppMutex"

[Setup]
; AppId INALTERADO desde a 0.7.x: é a chave que o Windows usa para reconhecer isto como
; atualização da versão instalada. Mudar geraria uma segunda entrada em "Adicionar ou
; remover programas" e duas cópias do app convivendo na máquina.
AppId={{9C2B4E7A-1F3D-4B8E-9A6C-0C1D2E3F4A5B}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; Os dois "no" abaixo existem por causa da renomeação: por padrão o Inno reinstalaria
; na pasta e no grupo da versão anterior (...\Piloto), e o produto continuaria com o
; nome antigo no disco mesmo exibindo "Click Write". A limpeza do que ficou para trás
; está em CurStepChanged, mais abaixo.
UsePreviousAppDir=no
UsePreviousGroup=no
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=ClickWriteSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
; Atualização in-place: os dados do usuário (%LOCALAPPDATA%\Piloto) NÃO são tocados —
; lá ficam o banco, o histórico e os modelos (~2,6 GB). A pasta de dados mantém o nome
; antigo de propósito: renomeá-la custaria um novo download de 2,6 GB por máquina, e o
; caminho não aparece para o atendente. O AppMutex é a rede de segurança caso o app
; reabra depois do fechamento automático feito em [Code].
AppMutex={#MyAppMutex}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na área de trabalho"; GroupDescription: "Atalhos adicionais:"
Name: "startupicon"; Description: "Iniciar o {#MyAppName} com o Windows"; GroupDescription: "Inicialização:"

[Files]
; Saída do 'dotnet publish -c Release -r win-x64 --self-contained true -o publish/'
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Extensão do navegador (para carregar sem compactação ou distribuir por GPO)
Source: "..\extension\*"; DestDir: "{app}\extension"; Flags: recursesubdirs createallsubdirs ignoreversion
; Script de download dos modelos (para rodar direto na máquina de teste)
Source: "..\scripts\download-models.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
; Monitor de logs ao vivo (fase piloto: acompanhar o app em tempo real na máquina de teste)
Source: "..\scripts\monitor-logs.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Baixar modelos (Whisper + Gemma)"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoExit -ExecutionPolicy Bypass -NoProfile -File ""{app}\scripts\download-models.ps1"""; \
  WorkingDir: "{app}\scripts"; \
  Comment: "Baixa os modelos para %LOCALAPPDATA%\Piloto\models"
Name: "{group}\{#MyAppName} — Logs ao vivo"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-ExecutionPolicy Bypass -NoProfile -File ""{app}\scripts\monitor-logs.ps1"""; \
  WorkingDir: "{app}\scripts"; \
  Comment: "Acompanha em tempo real o que o {#MyAppName} está fazendo (logs do dia)"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: startupicon
; Entrada de inicialização da 0.7.x: apontava para o Piloto.exe, que a 1.0 remove.
; Sem apagar, o Windows tentaria abrir um executável inexistente a cada login.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueName: "{#NomeAntigo}"; ValueType: none; Flags: deletevalue

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
{ Fecha o app automaticamente antes de atualizar/desinstalar, com confirmação.
  Sem isso, o app na bandeja (que só se esconde ao fechar a janela) travaria os
  arquivos e a atualização falharia com "arquivo em uso".
  Mata os DOIS executáveis: numa máquina com a 0.7.x o processo ainda se chama
  Piloto.exe, e é justamente ele que precisa sair para a 1.0 entrar. }
function FecharAppSeNecessario(): Boolean;
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
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#ExeAntigo} /F', '',
       SW_HIDE, ewWaitUntilTerminated, Codigo);
  Sleep(800);
end;

function InitializeSetup(): Boolean;
begin
  Result := FecharAppSeNecessario();
end;

function InitializeUninstall(): Boolean;
begin
  Result := FecharAppSeNecessario();
end;

{ Remove o que a versão 0.7.x deixou para trás. Roda DEPOIS da instalação: se algo aqui
  falhar, a 1.0 já está no disco e funcionando — a sobra é cosmética, nunca um app
  quebrado. Os dados do usuário em %LOCALAPPDATA%\Piloto não entram nesta limpeza. }
procedure LimparInstalacaoAntiga();
var
  PastaAntiga, GrupoAntigo, AtalhoAntigo: String;
begin
  PastaAntiga := ExpandConstant('{autopf}\{#NomeAntigo}');
  { Guarda contra apagar a pasta nova caso alguém iguale os dois nomes no futuro. }
  if (CompareText(PastaAntiga, ExpandConstant('{app}')) <> 0) and DirExists(PastaAntiga) then
    DelTree(PastaAntiga, True, True, True);

  GrupoAntigo := ExpandConstant('{autoprograms}\{#NomeAntigo}');
  if DirExists(GrupoAntigo) then
    DelTree(GrupoAntigo, True, True, True);

  AtalhoAntigo := ExpandConstant('{autodesktop}\{#NomeAntigo}.lnk');
  if FileExists(AtalhoAntigo) then
    DeleteFile(AtalhoAntigo);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    LimparInstalacaoAntiga();
end;
