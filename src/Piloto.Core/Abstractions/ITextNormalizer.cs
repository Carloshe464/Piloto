namespace Piloto.Core.Abstractions;

/// <summary>
/// Normaliza o texto transcrito antes das regras: números falados → dígitos,
/// "arroba"/"ponto" em e-mails, espaçamentos, etc.
/// </summary>
public interface ITextNormalizer
{
    string Normalizar(string texto);
}
