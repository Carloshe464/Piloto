using System.Windows;
using System.Windows.Media;
using Piloto.Core.Models;

namespace Piloto.App.ViewModels;

/// <summary>Uma categoria de campo objetivo (Telefones, E-mails, ...) na aba "Dados extraídos".</summary>
public sealed class CategoriaCampoVm
{
    public string Titulo { get; init; } = "";
    public IReadOnlyList<CampoExtraidoVm> Valores { get; init; } = Array.Empty<CampoExtraidoVm>();

    public Visibility VisibilidadeVazio => Valores.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Um valor extraído, pronto para exibição. Mostra <b>de onde veio</b>: o atendente
/// precisa saber, antes de copiar para o cadastro, se aquilo é dado do Zendesk ou um
/// palpite do Whisper — e quão bom é o palpite.
/// </summary>
public sealed class CampoExtraidoVm
{
    /// <summary>Confiança abaixo disto é palpite: a ligação precisa ser ouvida para confirmar.</summary>
    private const double LimiarBaixaConfianca = 0.7;

    // Mesmas cores dos recursos de App.xaml — congeladas, porque cada valor da lista
    // consulta as suas.
    private static readonly Brush VerdeTexto = Congelar("#6EE7B7");
    private static readonly Brush VerdeFundo = Congelar("#10291E");
    private static readonly Brush AmareloTexto = Congelar("#FDE68A");
    private static readonly Brush AmareloFundo = Congelar("#3A2A08");
    private static readonly Brush AzulTexto = Congelar("#7DD3FC");
    private static readonly Brush AzulFundo = Congelar("#0C2C41");

    public required string Valor { get; init; }
    public required double Confianca { get; init; }
    public required FieldSource Origem { get; init; }
    public required string TrechoOrigem { get; init; }

    public bool DoCadastro => Origem == FieldSource.Extensao;

    private bool ConfiancaBaixa => !DoCadastro && Confianca < LimiarBaixaConfianca;

    public string RotuloOrigem => DoCadastro ? "Cadastro" : $"Ouvido · {Confianca:P0}";

    /// <summary>Verde = veio do cadastro do Zendesk; amarelo = ouvido com confiança
    /// baixa; azul = ouvido com confiança boa.</summary>
    public Brush CorOrigem => DoCadastro ? VerdeTexto : ConfiancaBaixa ? AmareloTexto : AzulTexto;

    public Brush FundoOrigem => DoCadastro ? VerdeFundo : ConfiancaBaixa ? AmareloFundo : AzulFundo;

    /// <summary>Linha de procedência: o trecho da fala que gerou o valor, ou a origem
    /// no Zendesk. É o que permite auditar sem reabrir o áudio.</summary>
    public string Procedencia => DoCadastro
        ? TrechoOrigem
        : $"na fala: \"{TrechoOrigem}\"" + (ConfiancaBaixa ? "  ⚠ confirmar no áudio" : "");

    public static CampoExtraidoVm De(ExtractedValue v) => new()
    {
        Valor = v.Valor,
        Confianca = v.Confianca,
        Origem = v.Origem,
        TrechoOrigem = v.TrechoOrigem,
    };

    private static Brush Congelar(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
