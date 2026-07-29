using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Piloto.LogMonitor;

public partial class MainWindow : Window
{
    private static readonly Regex Linha = new(
        @"^(?<hora>\d{2}:\d{2}:\d{2}) \[(?<tipo>[^\]]+)\] (?<modulo>[^:]+): (?<texto>.*)$",
        RegexOptions.Compiled);

    private readonly string _pastaLogs = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piloto", "logs");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private List<LogEntry> _todos = new();
    private string _assinatura = "";
    private readonly Dictionary<DateTime, int> _ocultarPrimeiros = new();

    public ObservableCollection<LogEntry> EventosVisiveis { get; } = new();
    public ObservableCollection<LogEntry> Erros { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        FiltroData.SelectedDate = DateTime.Today;
        FiltroModulo.Items.Add("Todos os módulos");
        FiltroModulo.SelectedIndex = 0;
        _timer.Tick += (_, _) => Carregar();
        _timer.Start();
        Carregar();
    }

    private void Carregar()
    {
        Directory.CreateDirectory(_pastaLogs);
        var data = DataSelecionada;
        var arquivo = Path.Combine(_pastaLogs, $"piloto-{data:yyyyMMdd}.log");
        var info = new FileInfo(arquivo);
        var assinatura = info.Exists
            ? $"{data:yyyyMMdd}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"
            : $"{data:yyyyMMdd}:ausente";
        if (assinatura == _assinatura) return;
        _assinatura = assinatura;
        var entradas = new List<LogEntry>();
        if (info.Exists)
            LerArquivo(arquivo, entradas);

        var ocultar = _ocultarPrimeiros.GetValueOrDefault(data);
        _todos = entradas.Skip(Math.Min(ocultar, entradas.Count)).ToList();
        AtualizarModulos();
        AplicarFiltro();
    }

    private DateTime DataSelecionada => (FiltroData.SelectedDate ?? DateTime.Today).Date;

    private static void LerArquivo(string arquivo, List<LogEntry> destino)
    {
        LogEntry? atual = null;
        try
        {
            using var stream = new FileStream(arquivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } linha)
            {
                var match = Linha.Match(linha);
                if (match.Success)
                {
                    atual = new LogEntry
                    {
                        Hora = match.Groups["hora"].Value,
                        Tipo = match.Groups["tipo"].Value.Trim(),
                        Modulo = NomeCurto(match.Groups["modulo"].Value.Trim()),
                        Descricao = match.Groups["texto"].Value.Trim(),
                    };
                    destino.Add(atual);
                }
                else if (atual is not null && !string.IsNullOrWhiteSpace(linha))
                    atual.Detalhes += (atual.Detalhes.Length == 0 ? "" : Environment.NewLine) + linha;
            }
        }
        catch (IOException) { }
    }

    private static string NomeCurto(string categoria)
        => categoria.Split('.').LastOrDefault() ?? categoria;

    private void AtualizarModulos()
    {
        var selecionado = (FiltroModulo.SelectedItem as string) ?? "Todos os módulos";
        var modulos = _todos.Select(x => x.Modulo).Distinct().OrderBy(x => x).ToList();
        var atuais = FiltroModulo.Items.Cast<object>().Select(x => x.ToString()).Skip(1).ToList();
        if (atuais.SequenceEqual(modulos)) return;
        FiltroModulo.Items.Clear();
        FiltroModulo.Items.Add("Todos os módulos");
        foreach (var modulo in modulos) FiltroModulo.Items.Add(modulo);
        FiltroModulo.SelectedItem = FiltroModulo.Items.Cast<object>()
            .FirstOrDefault(x => x.ToString() == selecionado) ?? FiltroModulo.Items[0];
    }

    private void AplicarFiltro()
    {
        var nivel = (FiltroNivel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos os níveis";
        var modulo = FiltroModulo.SelectedItem?.ToString() ?? "Todos os módulos";
        var busca = Busca.Text.Trim();
        var filtrados = _todos.Where(x =>
            (nivel == "Todos os níveis" || x.Tipo.Equals(nivel, StringComparison.OrdinalIgnoreCase)) &&
            (modulo == "Todos os módulos" || x.Modulo == modulo) &&
            (busca.Length == 0 || x.Descricao.Contains(busca, StringComparison.OrdinalIgnoreCase)
                               || x.Detalhes.Contains(busca, StringComparison.OrdinalIgnoreCase)));

        Substituir(EventosVisiveis, filtrados);
        Substituir(Erros, _todos.Where(x => x.EhErro));
        TotalEventos.Text = _todos.Count.ToString();
        TotalErros.Text = Erros.Count.ToString();
        TotalGravacoes.Text = _todos.Count(x => x.Descricao.Contains("iniciada", StringComparison.OrdinalIgnoreCase)
                                                && x.Descricao.Contains("grava", StringComparison.OrdinalIgnoreCase)).ToString();
        TotalEnvios.Text = _todos.Count(x => x.Descricao.Contains("enviada", StringComparison.OrdinalIgnoreCase)
                                            || x.Descricao.Contains("aceita pelo servidor", StringComparison.OrdinalIgnoreCase)).ToString();
        Status.Text = $"{EventosVisiveis.Count} evento(s) em {DataSelecionada:dd/MM/yyyy} | Pasta: {_pastaLogs}";
    }

    private static void Substituir(ObservableCollection<LogEntry> destino, IEnumerable<LogEntry> origem)
    {
        destino.Clear();
        foreach (var item in origem) destino.Add(item);
    }

    private void Evento_Selecionado(object sender, SelectionChangedEventArgs e)
    {
        if (TabelaEventos.SelectedItem is not LogEntry item) return;
        TituloDetalhe.Text = $"{item.Hora} | {item.Modulo} | {item.Tipo}";
        DetalheEvento.Text = item.Descricao + (item.Detalhes.Length > 0 ? Environment.NewLine + item.Detalhes : "");
    }

    private void Erro_Selecionado(object sender, SelectionChangedEventArgs e)
    {
        if (TabelaErros.SelectedItem is not LogEntry item) return;
        Abas.SelectedIndex = 0;
        TabelaEventos.SelectedItem = item;
        TabelaEventos.ScrollIntoView(item);
    }

    private void Atualizar_Click(object sender, RoutedEventArgs e)
    {
        _assinatura = "";
        if (DataSelecionada != DateTime.Today)
            FiltroData.SelectedDate = DateTime.Today;
        else
            Carregar();
    }
    private void Limpar_Click(object sender, RoutedEventArgs e)
    {
        var data = DataSelecionada;
        _ocultarPrimeiros[data] = _ocultarPrimeiros.GetValueOrDefault(data) + _todos.Count;
        _todos.Clear();
        DetalheEvento.Clear();
        TituloDetalhe.Text = "Detalhes do evento selecionado";
        AplicarFiltro();
    }
    private void FiltroData_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _assinatura = "";
        DetalheEvento.Clear();
        TituloDetalhe.Text = "Detalhes do evento selecionado";
        Carregar();
    }
    private void ExibirErros_Click(object sender, RoutedEventArgs e) => Abas.SelectedIndex = 2;
    private void Filtro_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) AplicarFiltro(); }
    private void Busca_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) AplicarFiltro(); }

    private void AbrirPasta_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_pastaLogs);
        Process.Start(new ProcessStartInfo("explorer.exe", _pastaLogs) { UseShellExecute = true });
    }
}
