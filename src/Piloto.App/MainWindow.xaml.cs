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
    private readonly IModelCatalog _modelos;
    private readonly RecordingCoordinator _coordinator;
    private readonly ConfigService _config;
    private readonly ILogger<MainWindow> _log;

    private readonly ObservableCollection<CallRowVm> _linhas = new();
    private readonly DispatcherTimer _timer;

    public MainWindow(
        ICallRepository repo,
        IExporter exporter,
        IModelCatalog modelos,
        RecordingCoordinator coordinator,
        ConfigService config,
        ILogger<MainWindow> log)
    {
        _repo = repo;
        _exporter = exporter;
        _modelos = modelos;
        _coordinator = coordinator;
        _config = config;
        _log = log;

        InitializeComponent();

        ListaChamadas.ItemsSource = _linhas;
        _coordinator.EstadoGravacaoMudou += (_, gravando) => Dispatcher.Invoke(() => AtualizarBotoes(gravando));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => AtualizarContadores();
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
                var id = _coordinator.PararEEnfileirar();
                TxtStatus.Text = $"Chamada enfileirada (#{id}). Será transcrita em segundo plano.";
            }
            else
            {
                _coordinator.Iniciar();
                TxtStatus.Text = "Gravando… clique em Parar para transcrever.";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao alternar gravação");
            MessageBox.Show("Não foi possível acessar o áudio:\n\n" + ex.Message,
                "Piloto", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var win = new SettingsWindow(_config, _modelos) { Owner = this };
        win.ShowDialog();
        AtualizarBanner();
    }

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

        var win = new DetailWindow(registro, _exporter) { Owner = this };
        win.ShowDialog();
    }

    // ---------------------------------------------------------------- Interno

    private void AtualizarBotoes(bool gravando)
    {
        BtnGravar.Content = gravando ? "■ Parar e transcrever" : "● Iniciar gravação";
        BtnNaoGravar.IsEnabled = gravando;
    }

    private void AtualizarBanner()
    {
        if (_modelos.PipelinePronto)
        {
            BannerModelos.Visibility = Visibility.Collapsed;
            return;
        }
        var ausentes = string.Join(", ", _modelos.ModelosAusentes());
        TxtBanner.Text = $"Modelos ausentes ({ausentes}). A fila fica pausada até baixá-los "
                         + "(scripts/download-models.ps1) ou apontar a pasta de modelos.";
        BannerModelos.Visibility = Visibility.Visible;
    }

    private void AtualizarContadores()
    {
        try
        {
            var c = _repo.Contadores();
            var fila = _repo.ContarPendentes();
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
