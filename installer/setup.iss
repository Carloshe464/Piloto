; Inno Setup 6 — instalador do Click Write (publish self-contained; não exige .NET na máquina destino).
; Gera installer\Output\ClickWriteSetup-<versao>.exe. Rode via scripts\build-installer.ps1 ou no CI.

#define MyAppName "Click Write"
; 1.1 — a transcrição passou para o servidor. O aplicativo grava, envia e abre o
; resultado no navegador. Saem daqui os modelos (~2,6 GB) e todo o processamento local.
#define MyAppVersion "1.1.1"
#define MyAppPublisher "Click Write"
#define MyAppExeName "ClickWrite.exe"
#define LogMonitorExe "ClickWrite.LogMonitor.exe"
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
AppMutex={#MyAppMutex},ClickWriteLogMonitorMutex

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
; O monitor agora é um executável WPF publicado junto com o aplicativo.
; download-models.ps1 NÃO é mais distribuído: não há modelo local para baixar.

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} - Monitor de atividades"; Filename: "{app}\{#LogMonitorExe}"; \
  WorkingDir: "{app}"; Comment: "Mostra gravações, envios, avisos e erros do {#MyAppName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{app}\scripts\monitor-logs.ps1"
Type: files; Name: "{group}\{#MyAppName} - Logs ao vivo.lnk"
Type: files; Name: "{group}\{#MyAppName} — Logs ao vivo.lnk"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: startupicon
; Entrada de inicialização da 0.7.x: apontava para o Piloto.exe, que a 1.0 remove.
; Sem apagar, o Windows tentaria abrir um executável inexistente a cada login.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueName: "{#NomeAntigo}"; ValueType: none; Flags: deletevalue

[Run]
; A etapa de download de modelos saiu na 1.1: não há inferência nesta máquina.
; O aplicativo grava, envia ao servidor e abre o resultado no navegador.
Filename: "{app}\{#MyAppExeName}"; Description: "Executar o {#MyAppName} agora"; \
  Flags: nowait postinstall skipifsilent

[Code]
var
  PaginaServidor: TInputQueryWizardPage;

{ Pergunta o endereço do servidor e o token do atendente.

  Existe para que ninguém precise editar JSON com permissão de administrador depois de
  instalar. O instalador NÃO escreve a configuração do usuário direto: ele roda elevado,
  e localappdata sob elevação aponta para o perfil do administrador, não para o do
  atendente. Então grava servidor.json na pasta do programa e o app aplica na primeira
  abertura, já rodando como o usuário certo.

  Em instalação silenciosa (GPO), os valores vêm por /SERVIDOR= e /TOKEN=. }
procedure CriarPaginaServidor();
begin
  PaginaServidor := CreateInputQueryPage(wpSelectTasks,
    'Servidor de transcrição',
    'Onde esta máquina envia as ligações',
    'O Click Write grava a ligação e envia para o servidor, que devolve a transcrição e ' +
    'o resumo. Informe o endereço do servidor e o token deste atendente. ' +
    'Ambos podem ser alterados depois em Configurações.');

  PaginaServidor.Add('Endereço do servidor (ex.: http://192.168.0.10:8517)', False);
  PaginaServidor.Add('Token deste atendente', False);

  PaginaServidor.Values[0] := ExpandConstant('{param:SERVIDOR|http://servidor:8517}');
  PaginaServidor.Values[1] := ExpandConstant('{param:TOKEN|}');
end;

{ Testa o endereço antes de deixar avançar. Alcançar e ser recusado é um problema
  diferente de não alcançar, e cada um manda procurar num lugar diferente — dizer qual
  dos dois é economiza a ida e volta de descobrir isso depois de instalar. }
function TestarServidor(Url, Token: String; var Mensagem: String): Boolean;
var
  Http: Variant;
  Status: Integer;
begin
  Result := False;
  try
    Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    Http.SetTimeouts(5000, 8000, 8000, 8000);
    Http.Open('GET', Url + '/v1/health', False);
    if Token <> '' then
      Http.SetRequestHeader('X-Token', Token);
    Http.Send('');
    Status := Http.Status;

    if (Status >= 200) and (Status < 300) then
    begin
      Result := True;
      Mensagem := '';
    end
    else if Status = 401 then
      Mensagem := 'O servidor respondeu, mas recusou o token.' + #13#10 +
                  'Confira o token com quem administra o servidor.'
    else
      Mensagem := 'O servidor respondeu HTTP ' + IntToStr(Status) + '.' + #13#10 +
                  'Confira o endereço informado.';
  except
    Mensagem := 'Não foi possível alcançar o servidor neste endereço.' + #13#10 +
                'Verifique o endereço, a rede e se o servidor está ligado.';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Url, Token, Erro: String;
begin
  Result := True;
  if CurPageID <> PaginaServidor.ID then
    Exit;

  Url := Trim(PaginaServidor.Values[0]);
  Token := Trim(PaginaServidor.Values[1]);

  if Url = '' then
  begin
    MsgBox('Informe o endereço do servidor.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if TestarServidor(Url, Token, Erro) then
    Exit;

  { Instalar com a máquina offline é legítimo — a configuração fica gravada e passa a
    valer quando a rede voltar. O aviso informa, não bloqueia. }
  Result := MsgBox(Erro + #13#10 + #13#10 +
                   'Instalar assim mesmo? A configuração fica salva e pode ser ' +
                   'corrigida depois em Configurações.',
                   mbConfirmation, MB_YESNO) = IDYES;
end;

function EscaparJson(Valor: String): String;
begin
  StringChangeEx(Valor, '\', '\\', True);
  StringChangeEx(Valor, '"', '\"', True);
  Result := Valor;
end;

{ Grava o que foi perguntado. O app lê na primeira abertura e aplica à configuração do
  usuário — comparando a data deste arquivo, para não sobrescrever o que o atendente
  ajustar depois na tela. }
procedure GravarConfiguracaoServidor();
var
  Destino, Conteudo: String;
begin
  if not Assigned(PaginaServidor) then
    Exit;

  Destino := ExpandConstant('{app}\config\servidor.json');
  ForceDirectories(ExpandConstant('{app}\config'));

  Conteudo :=
    '{' + #13#10 +
    '  "url": "' + EscaparJson(Trim(PaginaServidor.Values[0])) + '",' + #13#10 +
    '  "token": "' + EscaparJson(Trim(PaginaServidor.Values[1])) + '"' + #13#10 +
    '}' + #13#10;

  SaveStringToFile(Destino, Conteudo, False);
end;

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
  begin
    if not CheckForMutexes('ClickWriteLogMonitorMutex') then
      Exit;
  end;

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
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#LogMonitorExe} /F', '',
       SW_HIDE, ewWaitUntilTerminated, Codigo);
  Sleep(800);
end;

function InitializeSetup(): Boolean;
begin
  Result := FecharAppSeNecessario();
end;

procedure InitializeWizard();
begin
  CriarPaginaServidor();
end;

function InitializeUninstall(): Boolean;
begin
  Result := FecharAppSeNecessario();
end;

{ Remove o que a versão 0.7.x deixou para trás. Roda DEPOIS da instalação: se algo aqui
  falhar, a nova versão já está no disco e funcionando — a sobra é cosmética, nunca um
  app quebrado. O banco e o histórico do usuário não entram nesta limpeza. }
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

{ Remove o que a inferência local deixou na pasta do programa. Uma atualização in-place
  não apaga arquivos que a nova versão não instala: sem esta limpeza, o executável do
  worker e as bibliotecas nativas do llama.cpp e do Whisper ficariam para sempre no
  disco. São justamente as DLLs que o antivírus bloqueava — não têm mais razão de existir
  aqui, e deixá-las só dá margem a alarme falso. }
procedure LimparInferenciaLocal();
var
  App, Modelos: String;
begin
  App := ExpandConstant('{app}');

  { Executor de inferência em processo próprio (0.7.11 em diante). }
  DelTree(App + '\Piloto.LlmWorker.exe', False, True, False);
  DelTree(App + '\Piloto.LlmWorker.*', False, True, False);

  { Bibliotecas nativas do llama.cpp e do Whisper, e os backends por instrução de CPU. }
  DelTree(App + '\llama*.dll', False, True, False);
  DelTree(App + '\ggml*.dll', False, True, False);
  DelTree(App + '\whisper*.dll', False, True, False);
  DelTree(App + '\LLamaSharp*.dll', False, True, False);
  DelTree(App + '\Whisper.net*.dll', False, True, False);
  DelTree(App + '\runtimes\*', False, True, True);

  { Script de download de modelos: não há mais o que baixar. }
  DelTree(App + '\scripts\download-models.ps1', False, True, False);

  // Modelos (~2,6 GB). Só funciona quando quem instala é o próprio usuário do
  // computador: com PrivilegesRequired=admin, a constante localappdata pode
  // apontar para o perfil do administrador, e não para o do atendente. Por isso
  // o aplicativo repete esta limpeza na primeira abertura, aí sim rodando como
  // o usuário certo.
  //
  // Comentário de linha de propósito: nenhum dos dois comentários de bloco do
  // Pascal aninha, e ambos têm delimitador de fechamento que aparece no meio de
  // texto técnico. Comentário de linha não tem como ser fechado cedo demais.
  Modelos := ExpandConstant('{localappdata}\{#NomeAntigo}\models');
  if DirExists(Modelos) then
    DelTree(Modelos, True, True, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    GravarConfiguracaoServidor();
    LimparInstalacaoAntiga();
    LimparInferenciaLocal();
  end;
end;
