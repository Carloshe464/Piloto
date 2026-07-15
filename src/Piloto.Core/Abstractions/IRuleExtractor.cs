using Piloto.Core.Models;

namespace Piloto.Core.Abstractions;

/// <summary>
/// Camada 1: extrai campos objetivos (telefone, CPF, e-mail, datas, valores, protocolo)
/// por regex/dicionário, atribuindo confiança a cada detecção.
/// </summary>
public interface IRuleExtractor
{
    ObjectiveFields Extrair(Transcript transcript);
}
