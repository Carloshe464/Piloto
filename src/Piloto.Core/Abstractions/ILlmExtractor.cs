using Piloto.Core.Configuration;
using Piloto.Core.Models;

namespace Piloto.Core.Abstractions;

/// <summary>
/// Camada 2: LLM local. Recebe a transcrição e as listas fechadas e devolve o resumo
/// interpretativo com saída JSON forçada. Temperatura 0. Não inventa dados.
/// </summary>
public interface ILlmExtractor
{
    Task<LlmSummary> ResumirAsync(Transcript transcript, ListasFechadas listas, CancellationToken ct = default);

    /// <summary>
    /// Libera os pesos da memória (recarregados na próxima chamada). Devolve true se havia
    /// algo carregado. Chamado pela fila após ociosidade — os ~2,4 GB do modelo não ficam
    /// residentes o dia inteiro na máquina do atendente.
    /// </summary>
    bool LiberarModelo() => false;
}
