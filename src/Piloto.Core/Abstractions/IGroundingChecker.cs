using Piloto.Core.Models;

namespace Piloto.Core.Abstractions;

/// <summary>
/// Camada 3: grounding. Garante que valores objetivos apontados pelo LLM realmente
/// existem na transcrição; caso contrário, zera (null) e marca o registro para revisão.
/// Também valida os campos de lista fechada.
/// </summary>
public interface IGroundingChecker
{
    void Aplicar(CallRecord registro, Configuration.ListasFechadas listas);
}
