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
}
