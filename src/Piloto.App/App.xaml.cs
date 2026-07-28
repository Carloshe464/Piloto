using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Piloto.App.Services;
using Piloto.Bridge;
using Piloto.Core.Abstractions;
using Piloto.Core.Pipeline;
using Piloto.Core.Services;

namespace Piloto.App;

public partial class App : Application
{
    /// <summary>Nome do mutex de instância única — o instalador (setup.iss) usa o mesmo
    /// nome em CheckForMutexes para detectar o app em execução antes de atualizar.
    /// <para><b>Não renomear junto com o produto.</b> É por este nome que o instalador da
    /// 1.0 (Click Write) reconhece uma 0.7.x (Piloto) rodando e a fecha antes de
    /// atualizar. Trocá-lo cegaria o instalador na atualização das máquinas em campo.</para></summary>
    private const string NomeMutex = "PilotoAppMutex";

    private ServiceProvider? _provider;
    private ConfigService? _config;
    private TrayIconController? _tray;
    private MainWindow? _main;
    private ILogger<App>? _log;
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: false, NomeMutex, out var primeiraInstancia);
        if (!primeiraInstancia)
        {
            // Duas instâncias consumiriam a mesma fila SQLite em paralelo (itens duplicados)
            // e a segunda falharia na porta do bridge. Bandeja + autostart tornam isso comum.
            MessageBox.Show("O Click Write já está em execução — procure o ícone na bandeja, ao lado do relógio.",
                "Click Write", MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnUnhandled;

        try
        {
            _config = ConfigService.Inicializar();
            _provider = CompositionRoot.Build(_config);
            _log = _provider.GetRequiredService<ILogger<App>>();

            // Primeira linha de cada sessão: sem ela não dá para saber pelo log qual
            // versão rodou — já perdemos diagnóstico de campo comparando log de versão
            // velha achando que era a nova.
            _log.LogInformation("Click Write {Versao} iniciado",
                typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?");

            var repo = _provider.GetRequiredService<ICallRepository>();
            repo.Inicializar();

            var coordinator = _provider.GetRequiredService<RecordingCoordinator>();
            var queue = _provider.GetRequiredService<QueueProcessor>();
            var bridge = _provider.GetRequiredService<ZendeskBridgeServer>();

            LimparModelosLocais();

            // Máquina desligada com ligação retida, ou servidor que passou a noite fora:
            // sobe o que ficou para trás assim que o app abre, sem esperar o timer.
            var uploader = _provider.GetRequiredService<ClickWriteUploader>();
            var retidas = uploader.PendentesEmDisco();
            if (retidas > 0)
            {
                _log.LogInformation("{Retidas} ligação(ões) retida(s) em disco — reenviando", retidas);
                _ = Task.Run(() => uploader.DrenarPendentesAsync());
            }

            _main = _provider.GetRequiredService<MainWindow>();

            _tray = new TrayIconController(
                abrir: MostrarPrincipal,
                alternarGravacao: () => _main!.AlternarGravacao(),
                naoGravar: () => _main!.NaoGravar(),
                configuracoes: () => _main!.AbrirConfiguracoes(),
                sair: Encerrar);

            coordinator.EstadoGravacaoMudou += (_, gravando) =>
                Dispatcher.Invoke(() => _tray!.AtualizarGravacao(gravando));

            coordinator.AvisoCaptura += (_, msg) =>
                Dispatcher.Invoke(() => _tray!.Notificar("Click Write — captura de áudio", msg));

            coordinator.ChamadaEnviada += (_, resposta) => Dispatcher.Invoke(() =>
            {
                var fila = resposta.Posicao is > 0 ? $" (posição {resposta.Posicao} na fila)" : "";
                _main!.MostrarStatus($"Ligação enviada ao servidor{fila}.");
                _tray?.Notificar("Click Write", $"Ligação enviada{fila}.");

                // O aplicativo não desenha tela de resultado: quem exibe é o servidor.
                if (_config.Settings.Servidor.AbrirResultadoNoNavegador)
                    AbrirNoNavegador(
                        _provider!.GetRequiredService<ClickWriteUploader>()
                                  .UrlDoResultado(resposta.CallId));
            });

            coordinator.EnvioAdiado += (_, pasta) => Dispatcher.Invoke(() =>
            {
                // Rede caiu ou servidor fora: a gravação NÃO se perde, fica em disco.
                _main!.MostrarStatus("Servidor inacessível — gravação guardada; será enviada sozinha.");
                _tray?.Notificar("Click Write — servidor inacessível",
                                 $"A ligação ficou guardada em {pasta} e sobe automaticamente.");
            });

            queue.ItemIniciado += (_, id) => Dispatcher.Invoke(() =>
                _main!.MostrarStatus($"Processando ligação #{id} em segundo plano — transcrição e resumo a caminho…"));

            queue.RegistroProcessado += (_, reg) => Dispatcher.Invoke(() =>
            {
                _tray!.Notificar("Click Write", reg.PrecisaRevisao
                    ? "Nova transcrição pronta — precisa de revisão"
                    : "Nova transcrição pronta");
                _main!.MostrarStatus($"Ligação #{reg.Id} pronta — transcrição e resumo disponíveis.");
                _main!.Recarregar();
            });

            try { bridge.Iniciar(); }
            catch (Exception ex) { _log.LogError(ex, "Falha ao iniciar o bridge na porta {Porta}", _config.Settings.Bridge.Porta); }

            queue.Iniciar();

            _ = Task.Run(() => AplicarRetencao(repo));

            MostrarPrincipal();
        }
        catch (Exception ex)
        {
            var arquivo = RegistrarErroInicializacao(ex);
            _log?.LogError(ex, "Falha ao iniciar o Click Write");
            MessageBox.Show(
                $"Falha ao iniciar o Click Write:\n\n[{ex.GetType().Name}] {ex.Message}\n\nDetalhes em:\n{arquivo}",
                "Click Write", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Grava o stack completo do erro de inicialização em um arquivo fácil de localizar.</summary>
    private static string RegistrarErroInicializacao(Exception ex)
    {
        try
        {
            // "Piloto" (nome antigo) permanece na PASTA DE DADOS de propósito: lá estão o
            // banco, o histórico e os modelos (~2,6 GB). Renomear custaria um novo download
            // por máquina, e o caminho não aparece para o atendente.
            var pasta = Path.Combine(
                Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%"), "Piloto", "logs");
            Directory.CreateDirectory(pasta);
            var arquivo = Path.Combine(pasta, "startup-error.txt");
            File.WriteAllText(arquivo, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}");
            return arquivo;
        }
        catch
        {
            return "(não foi possível gravar o arquivo de log)";
        }
    }

    /// <summary>
    /// Apaga os modelos de IA que a versão anterior baixou (~2,6 GB).
    /// <para>
    /// O instalador também tenta, mas roda elevado: <c>{localappdata}</c> pode apontar
    /// para o perfil do administrador em vez do perfil do atendente, e nesse caso os
    /// 2,6 GB continuariam ocupando o disco de quem usa a máquina. Aqui a limpeza roda
    /// como o usuário certo, na primeira abertura depois da atualização.
    /// </para>
    /// </summary>
    private void LimparModelosLocais()
    {
        try
        {
            var modelos = _config!.Settings.PastaModelos;
            if (!Directory.Exists(modelos))
                return;

            var bytes = new DirectoryInfo(modelos)
                .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            Directory.Delete(modelos, recursive: true);
            _log?.LogInformation(
                "Modelos locais removidos ({Mb} MB liberados) — a inferência agora é no servidor",
                bytes / 1024 / 1024);
        }
        catch (Exception e)
        {
            // Espaço em disco não vale derrubar a abertura do app.
            _log?.LogWarning(e, "Não foi possível remover os modelos locais");
        }
    }

    /// <summary>
    /// Abre a tela do servidor. Falha aqui é cosmética: a ligação já foi aceita e o
    /// registro existe — não vale derrubar o app porque o navegador não abriu.
    /// </summary>
    private void AbrirNoNavegador(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            _log?.LogWarning(e, "Não foi possível abrir {Url}", url);
        }
    }

    private void AplicarRetencao(ICallRepository repo)
    {
        try
        {
            var r = _config!.Settings.RetencaoDias;
            var resultado = repo.AplicarRetencao(r.Audio, r.Transcricao);
            _log?.LogInformation("Retenção: {Audios} áudios e {Regs} registros removidos",
                resultado.AudiosRemovidos, resultado.RegistrosRemovidos);
        }
        catch (Exception ex) { _log?.LogError(ex, "Falha ao aplicar retenção"); }
    }

    private void MostrarPrincipal()
    {
        if (_main is null) return;
        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void Encerrar()
    {
        _log?.LogInformation("Encerrando o Click Write");
        Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.LogError(e.Exception, "Exceção não tratada na UI");
        MessageBox.Show("Ocorreu um erro inesperado:\n\n" + e.Exception.Message,
            "Click Write", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Marca encerramento limpo: sem esta linha no log, o processo morreu (crash).
            _log?.LogInformation("Click Write encerrado normalmente");
            _tray?.Dispose();
            _provider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _mutex?.Dispose();
        }
        catch { /* ignore */ }
        base.OnExit(e);
    }
}
