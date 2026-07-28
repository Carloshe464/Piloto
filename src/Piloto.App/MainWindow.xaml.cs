using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Piloto.App.Services;
using Piloto.App.ViewModels;
using Piloto.App.Views;
using Piloto.Core.Abstractions;

namespace Piloto.App;

public partial class MainWindow : Window
{
    private readonly ICallRepository _repo;
    private readonly IExporter _exporter;
    private readonly Core.Services.SincronizadorServidor _sincronizador;
    private readonly RecordingCoordinator _coordinator;

    /// <summary>Última leitura do estado do servidor. A checagem é assíncrona; o banner
    /// lê este campo para não bloquear a interface a cada tique do relógio.</summary>
    private bool _servidorNoAr = true;
    private readonly Core.Services.ClickWriteUploader _uploader;
    private readonly ConfigService _config;
    private readonly ILogger<MainWindow> _log;

    private readonly ObservableCollection<CallRowVm> _linhas = new();
    private readonly DispatcherTimer _timer;

    public MainWindow(
        ICallRepository repo,
        IExporter exporter,
        Core.Services.SincronizadorServidor sincronizador,
        RecordingCoordinator coordinator,
        Core.Services.ClickWriteUploader uploader,
        ConfigService config,
        ILogger<MainWindow> log)
    {
        _repo = repo;
        _exporter = exporter;
        _sincronizador = sincronizador;
        _coordinator = coordinator;
        _uploader = uploader;
        _config = config;
        _log = log;

        InitializeComponent();

        ListaChamadas.ItemsSource = _linhas;
        _coordinator.EstadoGravacaoMudou += (_, gravando) => Dispatcher.Invoke(() => AtualizarBotoes(gravando));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) =>
        {
            AtualizarContadores();
            AtualizarBanner();
        };
        _timer.Start();

        Loaded += (_, _) =>
        {
            AtualizarBanner();
            AtualizarBotoes(_coordinator.EstaGravando);
            Recarregar();
        };
    }

    // -------------------------------------------------------- API p/ App/bandeja

    public void AlternarGravacao()
    {
        try
        {
            if (_coordinator.EstaGravando)
            {
                _coordinator.PararEEnviar();
                TxtStatus.Text = "Enviando ao servidor de transcrição…";
            }
            else
            {
                _coordinator.Iniciar();
                TxtStatus.Text = "Gravando… clique em Parar para enviar ao servidor.";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao alternar gravação");
            MessageBox.Show("Não foi possível acessar o áudio:\n\n" + ex.Message,
                "Click Write", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        AtualizarContadores();
    }

    public void NaoGravar()
    {
        if (!_coordinator.EstaGravando) return;
        _coordinator.Descartar();
        TxtStatus.Text = "Gravação descartada — nada foi salvo.";
    }

    public void AbrirConfiguracoes()
    {
        var win = new SettingsWindow(_config, _uploader) { Owner = this };
        win.ShowDialog();
        AtualizarBanner();
    }

    /// <summary>Atualiza a linha de status (usada pelo App para refletir a fila).</summary>
    public void MostrarStatus(string texto) => TxtStatus.Text = texto;

    public void Recarregar()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(Recarregar); return; }

        var termo = TxtBusca.Text?.Trim() ?? "";
        var registros = string.IsNullOrEmpty(termo) ? _repo.ListarRegistros() : _repo.Buscar(termo);

        _linhas.Clear();
        foreach (var r in registros) _linhas.Add(CallRowVm.De(r));
        AtualizarContadores();
    }

    // ---------------------------------------------------------------- Handlers

    private void BtnGravar_Click(object sender, RoutedEventArgs e) => AlternarGravacao();
    private void BtnNaoGravar_Click(object sender, RoutedEventArgs e) => NaoGravar();
    private void BtnConfig_Click(object sender, RoutedEventArgs e) => AbrirConfiguracoes();
    private void BtnAtualizar_Click(object sender, RoutedEventArgs e) => Recarregar();
    private void BtnBuscar_Click(object sender, RoutedEventArgs e) => Recarregar();

    private void TxtBusca_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Recarregar();
    }

    private void Lista_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListaChamadas.SelectedItem is not CallRowVm linha) return;
        var registro = _repo.ObterRegistro(linha.Id);
        if (registro is null) return;

        var win = new DetailWindow(registro, _exporter, _uploader) { Owner = this };
        win.ShowDialog();
        Recarregar(); // um reprocessamento pode ter sido enfileirado no detalhe
    }

    // ---------------------------------------------------------------- Interno

    private void AtualizarBotoes(bool gravando)
    {
        BtnGravar.Content = gravando ? "■ Parar e transcrever" : "● Iniciar gravação";
        BtnNaoGravar.IsEnabled = gravando;
    }

    /// <summary>
    /// O banner que avisava sobre modelos ausentes agora avisa sobre o servidor. É a
    /// mesma pergunta que ele sempre respondeu — "dá para processar agora?" — só que a
    /// resposta mudou de lugar junto com a inferência.
    /// <para>Gravar continua funcionando com o servidor fora do ar: a ligação fica
    /// retida em disco e sobe sozinha. O banner informa, não impede.</para>
    /// </summary>
    private void AtualizarBanner()
    {
        var retidas = _uploader.PendentesEmDisco();

        if (_servidorNoAr && retidas == 0)
            BannerModelos.Visibility = Visibility.Collapsed;
        else
        {
            TxtBanner.Text = _servidorNoAr
                ? $"{retidas} ligação(ões) aguardando envio ao servidor — subindo automaticamente."
                : "Servidor de transcrição inacessível. Pode gravar normalmente: as ligações "
                  + $"ficam guardadas nesta máquina ({retidas} no momento) e sobem sozinhas.";
            BannerModelos.Visibility = Visibility.Visible;
        }

        VerificarServidor();
    }

    /// <summary>Consulta o servidor sem travar a interface; o banner usa o resultado no
    /// tique seguinte.</summary>
    private async void VerificarServidor()
    {
        try { _servidorNoAr = await _uploader.ServidorNoArAsync(); }
        catch { _servidorNoAr = false; }
    }

    private void AtualizarContadores()
    {
        try
        {
            var c = _repo.Contadores();
            // "Fila" deixou de ser a fila local de transcrição: agora é o que ainda não
            // completou o caminho até o servidor — o que falta subir mais o que já subiu
            // e aguarda resultado.
            var fila = _uploader.PendentesEmDisco() + _sincronizador.AguardandoResultado();
            TxtTotal.Text = $"Chamadas: {c.TotalChamadas}";
            TxtTempo.Text = $"Tempo falado: {c.TempoTotalFalado:hh\\:mm\\:ss}";
            TxtRevisao.Text = $"A revisar: {c.PendentesRevisao}";
            TxtFila.Text = $"Fila: {fila}";
        }
        catch (Exception ex) { _log.LogDebug(ex, "Falha ao atualizar contadores"); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Fechar a janela apenas esconde para a bandeja; sair é pela opção "Sair".
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
