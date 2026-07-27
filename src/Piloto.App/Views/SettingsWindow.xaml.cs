using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Piloto.App.Services;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Remote;

namespace Piloto.App.Views;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _config;
    private readonly IModelCatalog _modelos;
    private readonly ServidorSaudeMonitor _servidor;

    public SettingsWindow(ConfigService config, IModelCatalog modelos, ServidorSaudeMonitor servidor)
    {
        _config = config;
        _modelos = modelos;
        _servidor = servidor;
        InitializeComponent();
        Carregar();
    }

    private void Carregar()
    {
        var s = _config.Settings;
        var listas = _config.CarregarListas();

        TxtMotivos.Text = string.Join(Environment.NewLine, listas.MotivoContato);
        TxtProdutos.Text = string.Join(Environment.NewLine, listas.Produto);
        TxtStatus.Text = string.Join(Environment.NewLine, listas.Status);
        TxtGlossario.Text = _config.CarregarGlossario();

        TxtRetAudio.Text = s.RetencaoDias.Audio.ToString(CultureInfo.InvariantCulture);
        TxtRetTransc.Text = s.RetencaoDias.Transcricao.ToString(CultureInfo.InvariantCulture);
        TxtPorta.Text = s.Bridge.Porta.ToString(CultureInfo.InvariantCulture);

        TxtServidorUrl.Text = s.Servidor.Url;
        TxtServidorToken.Password = s.Servidor.Token;
        TxtServidorTimeout.Text = s.Servidor.TimeoutSegundos.ToString(CultureInfo.InvariantCulture);
        TxtServidorTentativas.Text = s.Servidor.MaxTentativas.ToString(CultureInfo.InvariantCulture);

        ChkLlm.IsChecked = s.Llm.Habilitado;
        TxtLlmModelo.Text = s.Llm.Modelo;
        TxtLlmThreads.Text = s.Llm.Threads.ToString(CultureInfo.InvariantCulture);
        TxtLlmTemp.Text = s.Llm.Temperatura.ToString(CultureInfo.InvariantCulture);

        MostrarStatus();
    }

    /// <summary>
    /// Estado do servidor e do modelo local. As duas capacidades (<c>analiseDisponivel</c>
    /// e <c>resumoDisponivel</c>) aparecem porque são elas que explicam <b>quem</b> fez o
    /// trabalho na última ligação — sem isso, "o resumo não veio" não tem diagnóstico.
    /// </summary>
    private void MostrarStatus()
    {
        var llm = _modelos.LlmDisponivel ? "presente" : "AUSENTE";
        var linhas = new List<string> { $"Endereço: {_servidor.Endereco}" };

        if (_servidor.UltimoErro is { } erro)
        {
            linhas.Add($"Estado: INDISPONÍVEL — {erro}");
        }
        else if (_servidor.Ultima is { } saude)
        {
            linhas.Add($"Estado: no ar — {saude.Descricao}");
            linhas.Add($"Extração no servidor: {(saude.AnaliseDisponivel ? "sim" : "não (o app extrai localmente)")}"
                       + $"   •   Resumo no servidor: {(saude.ResumoDisponivel ? "sim" : "não (LLM local)")}");
            if (!saude.ContratoCompativel)
                linhas.Add($"Atenção: contrato {saude.VersaoContrato ?? "?"} ≠ {Core.Models.ServidorSaude.ContratoSuportado} — campos e resumo do servidor serão ignorados.");
        }
        else
        {
            linhas.Add("Estado: ainda não consultado — use \"Testar conexão\".");
        }

        linhas.Add($"Modelo de resumo (local): {llm}   •   Pasta: {_config.Settings.PastaModelos}");
        TxtStatusModelos.Text = string.Join("\n", linhas);
    }

    private async void TestarConexao_Click(object sender, RoutedEventArgs e)
    {
        // A URL/token editados aqui só valem no próximo início (o cliente HTTP é construído
        // uma vez, na subida); o teste consulta o servidor em uso agora — que é justamente
        // o que responde "salvei e continuo sem conexão?".
        BtnTestar.IsEnabled = false;
        try
        {
            await _servidor.AtualizarAsync().ConfigureAwait(true);
            MostrarStatus();
        }
        finally { BtnTestar.IsEnabled = true; }
    }

    private void AbrirPastaModelos_Click(object sender, RoutedEventArgs e)
    {
        var pasta = _config.Settings.PastaModelos;
        try
        {
            Directory.CreateDirectory(pasta);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{pasta}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível abrir a pasta:\n" + ex.Message, "Click Write",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Salvar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var listas = new ListasFechadas
            {
                MotivoContato = Linhas(TxtMotivos.Text),
                Produto = Linhas(TxtProdutos.Text),
                Status = Linhas(TxtStatus.Text),
            };
            _config.SalvarListas(listas);
            _config.SalvarGlossario(TxtGlossario.Text);

            var s = _config.Settings;
            s.RetencaoDias.Audio = ParseInt(TxtRetAudio.Text, s.RetencaoDias.Audio);
            s.RetencaoDias.Transcricao = ParseInt(TxtRetTransc.Text, s.RetencaoDias.Transcricao);
            s.Bridge.Porta = ParseInt(TxtPorta.Text, s.Bridge.Porta);
            s.Servidor.Url = TxtServidorUrl.Text.Trim();
            s.Servidor.Token = TxtServidorToken.Password.Trim();
            s.Servidor.TimeoutSegundos = ParseInt(TxtServidorTimeout.Text, s.Servidor.TimeoutSegundos);
            s.Servidor.MaxTentativas = ParseInt(TxtServidorTentativas.Text, s.Servidor.MaxTentativas);
            s.Llm.Habilitado = ChkLlm.IsChecked == true;
            s.Llm.Modelo = TxtLlmModelo.Text.Trim();
            s.Llm.Threads = ParseInt(TxtLlmThreads.Text, s.Llm.Threads);
            s.Llm.Temperatura = ParseFloat(TxtLlmTemp.Text, s.Llm.Temperatura);
            _config.SalvarSettings(s);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Falha ao salvar:\n\n" + ex.Message, "Click Write",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e) => Close();

    private static List<string> Linhas(string texto)
        => texto.Split('\n')
                .Select(l => l.Trim().TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();

    private static int ParseInt(string texto, int fallback)
        => int.TryParse(texto.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static float ParseFloat(string texto, float fallback)
        => float.TryParse(texto.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
